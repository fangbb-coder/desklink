// DeskLink.Panel —— 极简 MVVM 基础设施
//
// 不引入 CommunityToolkit.Mvvm：面板只需要 INotifyPropertyChanged + 一个 SetField。
// 为了三个文件的小便利多拉一个包不值当，也避免和 Client 的写法不一致。
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeskLink.Panel.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>赋值并在值真的变了时通知。返回是否发生变化。</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>状态横幅的严重级别。面板只有一个横幅，避免"到处都弹提示"变成噪音。</summary>
public enum BannerLevel { Info, Ok, Warn, Error }
