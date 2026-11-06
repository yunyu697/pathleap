using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Pathleap.Shared.Index;

/// <summary>一条搜索结果。路径按父链按需重建，只对最终返回的前 N 条做。</summary>
public sealed class SearchHit
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    public int Tier { get; set; }

    public double Score { get; set; }

    public int Depth { get; set; }
}

/// <summary>一次查询的完整结果（含诊断计数）。</summary>
public sealed class SearchOutcome
{
    public List<SearchHit> Hits { get; } = new();

    public int CandidateCount { get; set; }

    public int ScannedEntries { get; set; }

    public double ElapsedMs { get; set; }

    public string Query { get; set; } = string.Empty;

    public string TokenSummary { get; set; } = string.Empty;

    public int PrefixMatches { get; set; }

    public int SubstringMatches { get; set; }
}

/// <summary>
/// 索引查询：T0 完全相等 / T1 前缀 / T2 子串，多 token 按 AND 组合。
/// 排序策略：命中直接按 (tier, 深度) 分桶，桶内保持名字升序（扫描天然有序），
/// 取结果时从高 tier、浅深度依次拼接，避免对几十万候选做一次全排序。
/// </summary>
public sealed class SearchEngine
{
    public const int TierExact = 100;
    public const int TierPrefix = 85;
    public const int TierSubstring = 70;

    private const int DepthBuckets = 16;
    private const int BucketCapacity = 4096;
    private const int ParallelThreshold = 50_000;
    private const int PrimaryCandidateCap = 40_000;

    /// <summary>
    /// 每次扫描最多收录的子串命中数。分桶本身每桶上限 <see cref="BucketCapacity"/>，
    /// 这里再收紧是为了避免「一个高频首字节（如 .dll 的 '.'）命中几十万次」时
    /// 每个命中都要做一次偏移->条目定位，把开销压在扫描常量上。
    /// </summary>
    private const int PerPartitionMatchCap = 4_096;

    /// <summary>小索引（单线程扫描）放宽上限，避免影响真实目录下的完整结果。</summary>
    private const int SingleScanMatchCap = BucketCapacity * 4;

    private readonly IndexSnapshot _lpSnapshot;

    public SearchEngine(IndexSnapshot snapshot)
    {
        _lpSnapshot = snapshot;
    }

    public IndexSnapshot Snapshot => _lpSnapshot;

    public SearchOutcome Search(string query, int limit)
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpOutcome = new SearchOutcome { Query = query };
        var lpTokens = Tokenize(query);
        if (lpTokens.Count == 0)
        {
            lpOutcome.ElapsedMs = Elapsed(lpStarted);
            return lpOutcome;
        }

        var lpCount = _lpSnapshot.EntryCount;
        // 单 token 时前缀区间与子串区间互斥（子串扫描会跳过前缀区间），不会重复收录，
        // 因此不需要去重字典；多 token 才需要。
        var lpBuckets = new HitBuckets(trackTier: lpTokens.Count > 1);
        lpOutcome.ScannedEntries = lpCount;
        lpOutcome.TokenSummary = string.Join('+', lpTokens.Select(static t => Encoding.UTF8.GetString(t)));

        if (lpTokens.Count == 1)
        {
            CollectSingleToken(lpTokens[0], lpCount, lpBuckets, lpOutcome);
        }
        else
        {
            CollectMultiToken(lpTokens, lpCount, lpBuckets, lpOutcome);
        }

        lpOutcome.CandidateCount = lpBuckets.Count;
        var lpTake = Math.Min(Math.Clamp(limit, 1, 500), lpBuckets.Count);
        foreach (var (lpIndex, lpTier) in lpBuckets.EnumerateOrdered())
        {
            if (lpOutcome.Hits.Count >= lpTake)
            {
                break;
            }

            var lpRecord = _lpSnapshot.GetRecord(lpIndex);
            var lpHit = new SearchHit
            {
                Name = IndexSnapshot.DecodeName(_lpSnapshot.GetNameSpan(lpRecord)),
                Path = _lpSnapshot.BuildPath(lpIndex),
                IsDirectory = lpRecord.IsDirectory,
                Size = (long)lpRecord.Size,
                Depth = lpRecord.Depth
            };

            lpHit.Tier = lpTier;
            lpHit.Score = Math.Round(lpTier + 6.0 * (1.0 - Math.Min((int)lpRecord.Depth, 64) / 64.0), 2);
            if (!lpRecord.IsDirectory && lpRecord.ModifiedUnixSec > 0)
            {
                lpHit.ModifiedAt = DateTimeOffset.FromUnixTimeSeconds(lpRecord.ModifiedUnixSec).ToLocalTime();
            }

            lpOutcome.Hits.Add(lpHit);
        }

        lpOutcome.ElapsedMs = Elapsed(lpStarted);
        return lpOutcome;
    }

    private static double Elapsed(long startedTick)
    {
        return Math.Round((Stopwatch.GetTimestamp() - startedTick) * 1000.0 / Stopwatch.Frequency, 3);
    }

    private void CollectSingleToken(byte[] token, int count, HitBuckets buckets, SearchOutcome outcome)
    {
        var lpLow = LowerBound(token);
        var lpHigh = UpperBoundOfPrefix(token, lpLow);
        var lpPrefixAdded = 0;
        for (var lpI = lpLow; lpI < lpHigh; lpI++)
        {
            var lpRecord = _lpSnapshot.GetRecord(lpI);
            var lpTier = lpRecord.NameLength == token.Length ? TierExact : TierPrefix;
            buckets.Add(lpTier, lpRecord.Depth, lpI);
            lpPrefixAdded++;

            // 前缀桶全部填满后，后面的前缀条目已经进不了结果集，直接停止。
            if ((lpPrefixAdded & 0xFFF) == 0 && buckets.IsTierFull(TierPrefix))
            {
                break;
            }
        }

        outcome.PrefixMatches = lpPrefixAdded;

        var lpSubstring = FindSubstringMatches(token, count, lpLow, lpHigh);
        foreach (var lpIndex in lpSubstring)
        {
            buckets.Add(TierSubstring, _lpSnapshot.GetRecord(lpIndex).Depth, lpIndex);
        }

        outcome.SubstringMatches = lpSubstring.Count;
    }

    private void CollectMultiToken(List<byte[]> tokens, int count, HitBuckets buckets, SearchOutcome outcome)
    {
        // 先用首 token 收集候选，再逐个 token 校验；多 token 场景更少，放宽候选上限。
        var lpLow = LowerBound(tokens[0]);
        var lpHigh = UpperBoundOfPrefix(tokens[0], lpLow);
        var lpPrimary = new List<int>(Math.Min(PrimaryCandidateCap, count));
        for (var lpI = lpLow; lpI < lpHigh && lpPrimary.Count < PrimaryCandidateCap; lpI++)
        {
            lpPrimary.Add(lpI);
        }

        outcome.PrefixMatches = lpPrimary.Count;
        foreach (var lpIndex in FindSubstringMatches(tokens[0], count, lpLow, lpHigh))
        {
            if (lpPrimary.Count >= PrimaryCandidateCap)
            {
                break;
            }

            lpPrimary.Add(lpIndex);
        }

        outcome.SubstringMatches = lpPrimary.Count - outcome.PrefixMatches;

        foreach (var lpIndex in lpPrimary)
        {
            var lpRecord = _lpSnapshot.GetRecord(lpIndex);
            var lpName = _lpSnapshot.GetNameSpan(lpRecord);
            var lpBestTier = int.MaxValue;
            var lpAllMatched = true;
            foreach (var lpToken in tokens)
            {
                var lpTier = TierOf(lpName, lpToken);
                if (lpTier == 0)
                {
                    lpAllMatched = false;
                    break;
                }

                if (lpTier < lpBestTier)
                {
                    lpBestTier = lpTier;
                }
            }

            if (lpAllMatched)
            {
                buckets.Add(lpBestTier, lpRecord.Depth, lpIndex);
            }
        }
    }

    /// <summary>0 = 不匹配；100/85/70 对应三种 tier。</summary>
    private static int TierOf(ReadOnlySpan<byte> name, ReadOnlySpan<byte> foldedToken)
    {
        if (name.Length >= foldedToken.Length && StartsWith(name, foldedToken))
        {
            return name.Length == foldedToken.Length ? TierExact : TierPrefix;
        }

        return FoldContains(name, foldedToken) ? TierSubstring : 0;
    }

    /// <summary>
    /// 子串命中：直接在整块名字区（连续 130MB 级 UTF-8 缓冲）上做向量化首字节扫描，
    /// 命中后再二分定位条目并校验完整落在该名字内。这样避开了逐条目建 span 的开销，
    /// 500 万条目的扫描从“每条一次函数调用”降到“整块内存一次通过”。
    /// </summary>
    private List<int> FindSubstringMatches(byte[] token, int count, int skipLow, int skipHigh)
    {
        var lpResult = new List<int>(Math.Min(1024, count));
        var lpFirstValues = CreateFirstByteValues(token[0]);
        var lpPartitions = count < ParallelThreshold ? 1 : Environment.ProcessorCount * 2;
        var lpChunk = Math.Max(1, count / lpPartitions);

        if (lpPartitions == 1)
        {
            ScanArenaRange(token, lpFirstValues, 0, count, skipLow, skipHigh, lpResult, SingleScanMatchCap);
            return lpResult;
        }

        var lpLocal = new List<int>[lpPartitions];
        Parallel.For(0, lpPartitions, lpPart =>
        {
            var lpStart = lpPart * lpChunk;
            var lpEnd = lpPart == lpPartitions - 1 ? count : Math.Min(count, lpStart + lpChunk);
            var lpList = new List<int>(256);
            if (lpStart < lpEnd)
            {
                ScanArenaRange(token, lpFirstValues, lpStart, lpEnd, skipLow, skipHigh, lpList, PerPartitionMatchCap);
            }

            lpLocal[lpPart] = lpList;
        });

        // 分片按条目下标从小到大扫描，顺序拼接即可保持名字升序。
        for (var lpPart = 0; lpPart < lpPartitions; lpPart++)
        {
            lpResult.AddRange(lpLocal[lpPart]);
        }

        return lpResult;
    }

    private void ScanArenaRange(
        byte[] token,
        SearchValues<byte> firstValues,
        int startEntry,
        int endEntry,
        int skipLow,
        int skipHigh,
        List<int> target,
        int cap)
    {
        var lpArena = _lpSnapshot.GetArenaSpan();
        var lpFirstRecord = _lpSnapshot.GetRecord(startEntry);
        var lpLastRecord = _lpSnapshot.GetRecord(endEntry - 1);
        var lpByteStart = (int)lpFirstRecord.NameOffset;
        var lpByteEnd = (int)(lpLastRecord.NameOffset + lpLastRecord.NameLength);
        lpByteEnd = Math.Min(lpByteEnd, lpArena.Length);
        if (lpByteStart >= lpByteEnd)
        {
            return;
        }

        var lpSpan = lpArena[lpByteStart..lpByteEnd];
        var lpTokenLength = token.Length;
        var lpPos = 0;

        // 名字区按条目顺序连续排布、偏移升序，扫描位置单调递增，
        // 因此条目下标也用游标单调推进：每次命中只前进几次，避免每个命中都做 ~23 次二分的定位。
        var lpEntryCursor = startEntry;
        while (lpPos < lpSpan.Length)
        {
            var lpFound = lpSpan[lpPos..].IndexOfAny(firstValues);
            if (lpFound < 0)
            {
                return;
            }

            var lpAbsolute = lpPos + lpFound;
            lpPos = lpAbsolute + 1;
            if (lpAbsolute + lpTokenLength > lpSpan.Length)
            {
                return;
            }

            if (!FoldEquals(lpSpan.Slice(lpAbsolute, lpTokenLength), token))
            {
                continue;
            }

            var lpOffset = (uint)(lpByteStart + lpAbsolute);
            while (lpEntryCursor + 1 < endEntry
                   && _lpSnapshot.GetRecord(lpEntryCursor + 1).NameOffset <= lpOffset)
            {
                lpEntryCursor++;
            }

            // 跨条目边界的命中不算：必须完整落在该名字范围内。
            var lpRecord = _lpSnapshot.GetRecord(lpEntryCursor);
            if (lpOffset + (uint)lpTokenLength > lpRecord.NameOffset + lpRecord.NameLength)
            {
                continue;
            }

            if (lpEntryCursor >= skipLow && lpEntryCursor < skipHigh)
            {
                continue;
            }

            target.Add(lpEntryCursor);
            if (target.Count >= cap)
            {
                return;
            }
        }
    }

    private static SearchValues<byte> CreateFirstByteValues(byte foldedFirst)
    {
        var lpUpper = foldedFirst is >= (byte)'a' and <= (byte)'z' ? (byte)(foldedFirst - 32) : foldedFirst;
        if (lpUpper == foldedFirst)
        {
            return SearchValues.Create(stackalloc byte[] { foldedFirst });
        }

        return SearchValues.Create(stackalloc byte[] { foldedFirst, lpUpper });
    }

    /// <summary>按 ASCII 折叠比较定长片段。</summary>
    private static bool FoldEquals(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> foldedNeedle)
    {
        for (var lpI = 0; lpI < foldedNeedle.Length; lpI++)
        {
            if (AsciiLower(haystack[lpI]) != foldedNeedle[lpI])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>有序数组上比较“折叠名 vs 折叠查询”，返回第一个 >= token 的下标。</summary>
    private int LowerBound(byte[] foldedToken)
    {
        var lpLow = 0;
        var lpHigh = _lpSnapshot.EntryCount;
        while (lpLow < lpHigh)
        {
            var lpMid = (lpLow + lpHigh) >>> 1;
            if (CompareFolded(lpMid, foldedToken) < 0)
            {
                lpLow = lpMid + 1;
            }
            else
            {
                lpHigh = lpMid;
            }
        }

        return lpLow;
    }

    /// <summary>
    /// 第一个不再以 token 为前缀的下标。只在 [startLow, count) 内二分：
    /// 前缀区之前全是 false，从 0 起二分会让区间的“false 段”把结果挤到 0。
    /// </summary>
    private int UpperBoundOfPrefix(byte[] foldedToken, int startLow)
    {
        var lpLow = startLow;
        var lpHigh = _lpSnapshot.EntryCount;
        while (lpLow < lpHigh)
        {
            var lpMid = (lpLow + lpHigh) >>> 1;
            if (StartsWith(FoldedNameBytes(lpMid), foldedToken))
            {
                lpLow = lpMid + 1;
            }
            else
            {
                lpHigh = lpMid;
            }
        }

        return lpLow;
    }

    private int CompareFolded(int index, byte[] foldedToken)
    {
        return CompareBytes(FoldedNameBytes(index), foldedToken);
    }

    /// <summary>二分比较用的临时折叠数组（每次查询只做几十次，可忽略分配）。</summary>
    private byte[] FoldedNameBytes(int index)
    {
        var lpRecord = _lpSnapshot.GetRecord(index);
        var lpSpan = _lpSnapshot.GetNameSpan(lpRecord);
        var lpBytes = new byte[lpSpan.Length];
        for (var lpI = 0; lpI < lpSpan.Length; lpI++)
        {
            lpBytes[lpI] = AsciiLower(lpSpan[lpI]);
        }

        return lpBytes;
    }

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var lpCommon = Math.Min(left.Length, right.Length);
        for (var lpI = 0; lpI < lpCommon; lpI++)
        {
            if (left[lpI] != right[lpI])
            {
                return left[lpI] - right[lpI];
            }
        }

        return left.Length - right.Length;
    }

    private static bool StartsWith(ReadOnlySpan<byte> name, ReadOnlySpan<byte> prefix)
    {
        if (name.Length < prefix.Length)
        {
            return false;
        }

        for (var lpI = 0; lpI < prefix.Length; lpI++)
        {
            if (AsciiLower(name[lpI]) != prefix[lpI])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ASCII 大小写折叠的子串匹配；非 ASCII 字节原样比较（中文等不受影响）。</summary>
    internal static bool FoldContains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> foldedNeedle)
    {
        var lpNeedleLength = foldedNeedle.Length;
        if (lpNeedleLength == 0)
        {
            return true;
        }

        if (haystack.Length < lpNeedleLength)
        {
            return false;
        }

        var lpFirst = foldedNeedle[0];
        var lpLimit = haystack.Length - lpNeedleLength;
        for (var lpI = 0; lpI <= lpLimit; lpI++)
        {
            if (AsciiLower(haystack[lpI]) != lpFirst)
            {
                continue;
            }

            var lpMatched = true;
            for (var lpJ = 1; lpJ < lpNeedleLength; lpJ++)
            {
                if (AsciiLower(haystack[lpI + lpJ]) != foldedNeedle[lpJ])
                {
                    lpMatched = false;
                    break;
                }
            }

            if (lpMatched)
            {
                return true;
            }
        }

        return false;
    }

    private static byte AsciiLower(byte value)
    {
        return value >= (byte)'A' && value <= (byte)'Z' ? (byte)(value + 32) : value;
    }

    /// <summary>查询按空白切分；每个 token 转成小写 UTF-8（ASCII 折叠）。</summary>
    private static List<byte[]> Tokenize(string query)
    {
        var lpTokens = new List<byte[]>(4);
        foreach (var lpPart in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lpBytes = Encoding.UTF8.GetBytes(lpPart);
            for (var lpI = 0; lpI < lpBytes.Length; lpI++)
            {
                lpBytes[lpI] = AsciiLower(lpBytes[lpI]);
            }

            lpTokens.Add(lpBytes);
        }

        return lpTokens;
    }

    /// <summary>(tier, 深度) 分桶；桶内保持扫描顺序（名字升序），取结果时按 tier 与深度依次拼接。</summary>
    private sealed class HitBuckets
    {
        private readonly List<int>?[,] _lpLists = new List<int>?[3, DepthBuckets];
        private readonly Dictionary<int, int>? _lpTierOf;
        private readonly int[] _lpRemaining = new int[3];

        public HitBuckets(bool trackTier)
        {
            _lpTierOf = trackTier ? new Dictionary<int, int>(4_096) : null;
            for (var lpI = 0; lpI < 3; lpI++)
            {
                _lpRemaining[lpI] = DepthBuckets * BucketCapacity;
            }
        }

        public int Count { get; private set; }

        /// <summary>该 tier 的所有深度桶是否都已满（满则后续同类命中已不可能进入结果集）。</summary>
        public bool IsTierFull(int tier)
        {
            return _lpRemaining[TierSlot(tier)] <= 0;
        }

        public void Add(int tier, int depth, int index)
        {
            if (_lpTierOf is not null && _lpTierOf.TryGetValue(index, out var lpExisting))
            {
                // 同一 entry 已被更高 tier 收录（前缀阶段先于子串扫描），保留高 tier。
                if (lpExisting >= tier)
                {
                    return;
                }

                _lpTierOf[index] = tier;
                return;
            }

            var lpTierSlot = TierSlot(tier);
            var lpDepthSlot = Math.Min(depth, DepthBuckets - 1);
            _lpLists[lpTierSlot, lpDepthSlot] ??= new List<int>(64);
            var lpList = _lpLists[lpTierSlot, lpDepthSlot]!;
            if (lpList.Count >= BucketCapacity)
            {
                return;
            }

            lpList.Add(index);
            _lpTierOf?.Add(index, tier);
            _lpRemaining[lpTierSlot]--;
            Count++;
        }

        public IEnumerable<(int Index, int Tier)> EnumerateOrdered()
        {
            for (var lpTierSlot = 0; lpTierSlot < 3; lpTierSlot++)
            {
                var lpTier = lpTierSlot switch
                {
                    0 => TierExact,
                    1 => TierPrefix,
                    _ => TierSubstring
                };
                for (var lpDepthSlot = 0; lpDepthSlot < DepthBuckets; lpDepthSlot++)
                {
                    var lpList = _lpLists[lpTierSlot, lpDepthSlot];
                    if (lpList is null)
                    {
                        continue;
                    }

                    foreach (var lpIndex in lpList)
                    {
                        yield return (lpIndex, lpTier);
                    }
                }
            }
        }

        private static int TierSlot(int tier)
        {
            return tier switch
            {
                TierExact => 0,
                TierPrefix => 1,
                _ => 2
            };
        }
    }
}
