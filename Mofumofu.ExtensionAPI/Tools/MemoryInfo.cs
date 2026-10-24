namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 用于宏观调控内存的一个小工具接口
/// </summary>
public interface IMemoryInfo
{
    /// <summary>
    /// 返回当前内存的剩余量.
    /// </summary>
    /// <returns>内存剩余量(以MB为计量单位)</returns>
    public int MemoryRemaining();
}