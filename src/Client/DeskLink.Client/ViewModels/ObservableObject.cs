using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeskLink.Client.ViewModels;

/// <summary>
/// 极小的 <see cref="INotifyPropertyChanged"/> 基类。
///
/// 为什么手写而不引 CommunityToolkit.Mvvm：本机 NuGet 缓存里没有该包，
/// 而它带来的收益（源生成器省几个属性样板）远小于"为 P8 引入一个新依赖"的成本。
/// 一个 SetProperty 就够整个客户端用了。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
