using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using TradePet.App.Controls;
using TradePet.App.ViewModels;
using TradePet.App.Views;
using TradePet.Application.Review;
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
                var quickReviewItem = Assert.Single(window.ContextMenu.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "快速复盘"));
                Assert.Same(viewModel.ShowQuickReviewCommand, quickReviewItem.Command);
                var quickReviewCount = 0;
                viewModel.ShowQuickReviewAsync = () =>
                {
                    quickReviewCount++;
                    return Task.CompletedTask;
                };
                quickReviewItem.Command.Execute(null);
                Assert.Equal(1, quickReviewCount);
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
                VerifySetupFlow();
                VerifyQuickReview();
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

    private static void VerifyQuickReview()
    {
        var now = DateTimeOffset.UtcNow;
        var trade = new TradeRecord("Broker|1", 1, "TEST", TradeSide.Buy, now.AddMinutes(-10), now,
            new(2026, 9, 24), new(2026, 9, 24), 100, 99, 1, 1, 0, -5, true);
        var detail = new TradeDetailData(trade, [], null, null, null,
            [new PositionPnlSample(new TradeKey("Broker|1", 1), now.AddSeconds(-1), 0, 20, 20, 1,
                null, null, 1_000, "position-pnl-v1")], null, null, [], [], [], null,
            new ReviewDataVersion("Broker|1", 1, 1, 1, "rule-1", "time-1", now));
        var review = new QuickReviewWindow(detail);
        try
        {
            Assert.False(review.Topmost);
            Assert.False(review.ShowActivated);
            review.Show();
            review.UpdateLayout();
            Assert.Contains("浮盈回吐", review.ExitReason);
            Assert.Contains("回吐 25", review.AnalysisSummary);
            Assert.False(string.IsNullOrWhiteSpace(review.Improvement));
            ((TextBox)review.FindName("ExitReasonBox")).Text = "主动退出";
            ((TextBox)review.FindName("ImproveBox")).Text = "我的修正";
            Assert.Equal("主动退出", review.ExitReason);
            Assert.Equal("我的修正", review.Improvement);
            Assert.Contains("回吐 25", review.AnalysisSummary);
        }
        finally { review.Close(); }
    }

    private static void VerifySetupFlow()
    {
        var viewModel = new MainViewModel { SelectedTerminalPath = @"C:\TradePet-test-missing\terminal64.exe" };
        var saved = false;
        var setup = new SetupWindow(viewModel, () => Task.FromResult(saved)) { ShowActivated = false };
        try
        {
            setup.Show();
            setup.UpdateLayout();
            Assert.Equal(@"C:\TradePet-test-missing\terminal64.exe", viewModel.SelectedTerminalPath);
            var platform = (ComboBox)setup.FindName("PlatformPicker");
            platform.SelectedValue = TradePet.Infrastructure.Mt5.TradingPlatform.Mt4;
            Assert.Equal(TradePet.Infrastructure.Mt5.TradingPlatform.Mt4, viewModel.SelectedPlatform);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)setup.FindName("PythonPanel")).Visibility);
            var next = (Button)setup.FindName("NextButton");
            for (var i = 0; i < 3; i++) next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, ((StackPanel)setup.FindName("PreferencesStep")).Visibility);
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(next.IsEnabled);
            Assert.Contains("未能保存", ((TextBlock)setup.FindName("Status")).Text);
            saved = true;
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(next.IsEnabled);
            Assert.Contains("已保存", ((TextBlock)setup.FindName("Status")).Text);
        }
        finally { setup.Close(); }
    }

    private static void Invoke(PetWindow window, string method, params object[] arguments) =>
        typeof(PetWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);
}
