using System.Windows;
using System.Windows.Threading;

namespace EegAcquisition.Helpers;

public static class DispatcherHelper
{
    public static void RunOnUI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}
