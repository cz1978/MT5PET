using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using TradePet.App.Controls;
using TradePet.App.ViewModels;
using TradePet.App.Views;
using TradePet.Core.Domain;
using Xunit;

namespace TradePet.App.Tests;

[CollectionDefinition("Desktop window tests", DisableParallelization = true)]
public sealed class DesktopWindowTestCollection;

[Collection("Desktop window tests")]
public sealed class PetWindowTests
{
    [Fact]
    public async Task PetWindow_ExitMenuAndTransientUiRespectInteractionAndVisibility()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new App();
            PetWindow? window = null;
            try
            {
                app.InitializeComponent();
                var viewModel = new MainViewModel();
                window = new PetWindow(viewModel) { ShowActivated = false };
                window.Show();
                window.UpdateLayout();
                Assert.Equal(SystemParameters.WorkArea.Right - window.Width - 24, window.Left, 3);
                Assert.Equal(SystemParameters.WorkArea.Bottom - window.Height - 24, window.Top, 3);
                var sprite = (PetSpriteControl)window.FindName("PetSprite");
                var quickCard = (Popup)window.FindName("QuickCardPopup");
                var bubble = (Popup)window.FindName("SpeechBubblePopup");
                var timer = (DispatcherTimer)typeof(PetSpriteControl)
                    .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sprite)!;

                Assert.Equal(RenderMode.SoftwareOnly,
                    ((HwndTarget)PresentationSource.FromVisual(window).CompositionTarget).RenderMode);
                Assert.True(timer.IsEnabled);
                sprite.SetAnimationPaused(true);
                Assert.False(timer.IsEnabled);
                sprite.SetAnimationPaused(false);
                Assert.True(timer.IsEnabled);

                viewModel.IsBubbleVisible = true;
                Assert.True(bubble.IsOpen);
                window.Hide();
                Assert.False(bubble.IsOpen);
                Assert.False(timer.IsEnabled);
                viewModel.IsBubbleVisible = false;
                viewModel.IsBubbleVisible = true;
                Assert.False(bubble.IsOpen);
                window.Show();
                Assert.True(bubble.IsOpen);
                Assert.True(timer.IsEnabled);

                // A visible alert must not be left behind by autonomous movement.
                viewModel.PetActivity = PetActivity.RunningRight;
                var left = window.Left;
                Invoke(window, "MoveWithActivity");
                Assert.Equal(left, window.Left);

                typeof(PetWindow).GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, true);
                Invoke(window, "UpdateSpeechBubble");
                Assert.False(bubble.IsOpen);
                Invoke(window, "SetQuickCardVisible", true);
                Assert.False(quickCard.IsOpen);
                Invoke(window, "MoveWithActivity");
                Assert.Equal(left, window.Left);
                typeof(PetWindow).GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, false);

                // The quick card is hosted by a separate popup, so the pet window never grows
                // to include a stale card surface before an OS-level drag begins.
                viewModel.IsBubbleVisible = false;
                Invoke(window, "SetQuickCardVisible", true);
                Assert.True(quickCard.IsOpen);
                Assert.InRange(window.Width, sprite.Width + 15, sprite.Width + 17);
                Assert.InRange(window.Height,
                    Math.Max(230, sprite.Height + 28) - 1,
                    Math.Max(230, sprite.Height + 28) + 1);
                Invoke(window, "SetQuickCardVisible", false);
                Assert.False(quickCard.IsOpen);

                // A click temporarily closes the popup before DragMove determines that the
                // pointer did not move. The second click must toggle the pinned state instead
                // of treating that temporary close as a request to reopen the card.
                Invoke(window, "ToggleQuickCard");
                Assert.True(quickCard.IsOpen);
                Invoke(window, "SetQuickCardVisible", false);
                Assert.False(quickCard.IsOpen);
                Invoke(window, "ToggleQuickCard");
                Assert.False(quickCard.IsOpen);
                Assert.False((bool)typeof(PetWindow)
                    .GetField("_quickCardPinned", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!);

                window.ContextMenu.PlacementTarget = window;
                window.ContextMenu.IsOpen = true;
                window.ContextMenu.UpdateLayout();
                var exitItem = Assert.Single(window.ContextMenu.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "退出天禄交易助手"));
                Assert.Same(viewModel.ExitCommand, exitItem.Command);
                var exitCount = 0;
                viewModel.ExitApplicationAsync = () =>
                {
                    exitCount++;
                    return Task.CompletedTask;
                };
                exitItem.Command.Execute(null);
                Assert.Equal(1, exitCount);
                window.ContextMenu.IsOpen = false;

                window.DisposeTrayIcon();
                Assert.False(quickCard.IsOpen);
                viewModel.IsBubbleVisible = false;
                viewModel.IsBubbleVisible = true;
                sprite.SetAnimationPaused(false);
                Assert.False(timer.IsEnabled);
                Assert.False(bubble.IsOpen);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                window?.DisposeTrayIcon();
                window?.Close();
                app.Shutdown();
                completion.TrySetResult();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Invoke(PetWindow window, string method, params object[] arguments) =>
        typeof(PetWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);
}
