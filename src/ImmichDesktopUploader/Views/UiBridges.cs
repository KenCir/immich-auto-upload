using ImmichDesktopUploader.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Controls;
namespace ImmichDesktopUploader.Views;
public sealed class QueueDispatcher(DispatcherQueue queue) : IUiDispatcher
{ public bool Enqueue(Action action) => queue.TryEnqueue(() => action()); }
public sealed class ErrorSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
