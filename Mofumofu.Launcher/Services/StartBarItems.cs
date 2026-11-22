using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace Mofumofu.Launcher.Services;

public sealed class VersionChoice
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public Bitmap? Icon { get; init; }
}

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
