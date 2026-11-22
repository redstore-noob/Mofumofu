namespace Mofumofu.ExtensionAPI.Tools;

/// <summary>
/// 内存查询接口；返回值以 MB 为单位。
/// </summary>
public interface IMemoryInfo
{
    public int MemoryRemaining();
}