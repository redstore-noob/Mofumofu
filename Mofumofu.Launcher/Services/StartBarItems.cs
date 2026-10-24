using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace Mofumofu.Launcher.Services;

/// <summary>开始栏「版本」下拉里的一项。</summary>
public sealed class VersionChoice
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Bitmap? Icon { get; init; }
}

/// <summary>开始栏「账号」下拉里的一项（Head 为皮肤大头，异步补齐）。</summary>
public sealed class AccountChoice : INotifyPropertyChanged
{
    private Bitmap? _head;

    public string Name { get; init; } = string.Empty;

    public string DisplayName => Name;

    public Bitmap? Head
    {
        get => _head;
        set
        {
            if (ReferenceEquals(_head, value)) return;

            _head = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Head)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHead)));
        }
    }

    public bool HasHead => _head is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}
