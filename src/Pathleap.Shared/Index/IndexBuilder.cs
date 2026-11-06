using System.Text;

namespace Pathleap.Shared.Index;

/// <summary>
/// 索引构建器：统一承载目录遍历（降级）与 USN/MFT（主路径）两种来源的节点集合，
/// 支持按 FRN 定位与墓碑标记，落盘时统一重映射父链并过滤墓碑。
/// </summary>
public sealed class IndexBuilder
{
    private readonly List<IndexNode> _lpNodes = new();
    private readonly Dictionary<ulong, int> _lpByFrn = new(1024);
    private readonly Dictionary<int, ulong> _lpParentFrns = new(1024);
    private readonly HashSet<int> _lpDeleted = new();

    public int Count => _lpNodes.Count;

    public int DeletedCount => _lpDeleted.Count;

    public IndexNode this[int index] => _lpNodes[index];

    public int Add(IndexNode node)
    {
        if (string.IsNullOrEmpty(node.FoldedName))
        {
            node.FoldedName = FoldName(node.Name);
        }

        _lpNodes.Add(node);
        return _lpNodes.Count - 1;
    }

    public int AddWithFrn(ulong frn, IndexNode node)
    {
        var lpIndex = Add(node);
        if (frn != 0)
        {
            _lpByFrn[frn] = lpIndex;
        }

        return lpIndex;
    }

    public int? FindByFrn(ulong frn)
    {
        return _lpByFrn.TryGetValue(frn, out var lpIndex) ? lpIndex : null;
    }

    public void MarkDeleted(ulong frn)
    {
        var lpIndex = FindByFrn(frn);
        if (lpIndex is not null)
        {
            _lpDeleted.Add(lpIndex.Value);
        }
    }

    /// <summary>USN 记录只给出父 FRN，先暂存，枚举结束后统一解析成父下标。</summary>
    public void SetParentFrn(ulong frn, ulong parentFrn)
    {
        var lpIndex = FindByFrn(frn);
        if (lpIndex is not null)
        {
            _lpParentFrns[lpIndex.Value] = parentFrn;
        }
    }

    /// <summary>解析父 FRN -> 父下标并计算深度；找不到父节点或指向卷根的挂到 rootIndex。</summary>
    public void ResolveParents(ulong rootFrn, int rootIndex)
    {
        var lpDepthCache = new int[_lpNodes.Count];
        Array.Fill(lpDepthCache, -1);

        foreach (var (lpIndex, lpParentFrn) in _lpParentFrns)
        {
            if (lpIndex == rootIndex)
            {
                continue;
            }

            int lpParentIndex;
            if (lpParentFrn == rootFrn)
            {
                lpParentIndex = rootIndex;
            }
            else
            {
                var lpResolved = FindByFrn(lpParentFrn);
                lpParentIndex = lpResolved ?? rootIndex;
            }

            _lpNodes[lpIndex].ParentIndex = lpParentIndex;
        }

        foreach (var lpIndex in _lpParentFrns.Keys)
        {
            ComputeDepth(lpIndex, _lpNodes, lpDepthCache, rootIndex);
        }
    }

    private static int ComputeDepth(int index, List<IndexNode> nodes, int[] cache, int rootIndex)
    {
        if (cache[index] >= 0)
        {
            return cache[index];
        }

        var lpNode = nodes[index];
        if (lpNode.ParentIndex < 0 || lpNode.ParentIndex >= nodes.Count || lpNode.ParentIndex == index)
        {
            cache[index] = index == rootIndex ? 0 : 1;
            return cache[index];
        }

        // 用“正在计算”标记（-2）检测环，异常父链按 1 层处理。
        cache[index] = -2;
        var lpParentDepth = cache[lpNode.ParentIndex];
        if (lpParentDepth == -2)
        {
            cache[index] = 1;
            return 1;
        }

        cache[index] = ComputeDepth(lpNode.ParentIndex, nodes, cache, rootIndex) + 1;
        lpNode.Depth = cache[index];
        return cache[index];
    }

    public bool IsDeleted(int index) => _lpDeleted.Contains(index);

    /// <summary>把当前节点集合落盘成索引文件。墓碑节点剔除，父链按剩余节点重映射。</summary>
    public IndexBuildResult Save(string outputPath, string sourceLabel, int skippedCount, double buildMs)
    {
        // 先算出存活节点的新下标，再按新下标复制节点（不修改构建期的原始集合，允许重复落盘）。
        var lpRemap = new int[_lpNodes.Count];
        var lpLiveOldIndexes = new List<int>(_lpNodes.Count);
        for (var lpI = 0; lpI < _lpNodes.Count; lpI++)
        {
            if (_lpDeleted.Contains(lpI))
            {
                lpRemap[lpI] = IndexFormat.NoParent;
                continue;
            }

            lpRemap[lpI] = lpLiveOldIndexes.Count;
            lpLiveOldIndexes.Add(lpI);
        }

        var lpLive = new List<IndexNode>(lpLiveOldIndexes.Count);
        foreach (var lpOldIndex in lpLiveOldIndexes)
        {
            var lpOld = _lpNodes[lpOldIndex];
            var lpParent = IndexFormat.NoParent;
            if (lpOld.ParentIndex >= 0 && lpOld.ParentIndex < _lpNodes.Count)
            {
                lpParent = lpRemap[lpOld.ParentIndex];
            }

            var lpCopy = new IndexNode(lpOld.Name, lpParent, lpOld.Depth, lpOld.Flags, lpOld.Size, lpOld.ModifiedTicks);
            lpCopy.FoldedName = lpOld.FoldedName;
            lpLive.Add(lpCopy);
        }

        return IndexSnapshot.Write(lpLive, outputPath, sourceLabel, skippedCount, buildMs);
    }

    /// <summary>构建期统一的小写折叠（ASCII），排序与匹配都用这个键。</summary>
    public static string FoldName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var lpHasUpper = false;
        foreach (var lpChar in name)
        {
            if (lpChar is >= 'A' and <= 'Z')
            {
                lpHasUpper = true;
                break;
            }
        }

        // 全小写（含中文等非 ASCII）的名字直接复用原字符串，避免 500 万级条目多一份副本。
        if (!lpHasUpper)
        {
            return name;
        }

        var lpBuilder = new StringBuilder(name.Length);
        foreach (var lpChar in name)
        {
            lpBuilder.Append(lpChar is >= 'A' and <= 'Z' ? (char)(lpChar + 32) : lpChar);
        }

        return lpBuilder.ToString();
    }
}
