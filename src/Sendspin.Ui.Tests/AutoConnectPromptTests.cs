using Avalonia.Controls;
using Avalonia.Threading;
using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Client;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the auto-connect question: when it is asked, what each answer writes, and that it is
/// asked once per server.
/// </summary>
/// <remarks>
/// The player service writes <see cref="PlayerSettings.LastServerId"/> before it raises the
/// connection event, so the tests write it the same way and then apply the event.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class AutoConnectPromptTests(HeadlessSession headless)
{
    private static readonly ConnectionChangedEventArgs Connected = new(true, "Living Room");
    private static readonly ConnectionChangedEventArgs Disconnected = new(false, null);

    [Fact]
    public void ThePrompt_AppearsAfterADiscoverModeConnectionWhilePolicyIsNever() => headless.Run(() =>
    {
        using var shell = Shell.Show(Discover);
        var prompt = shell.Find<Border>("AutoConnectPrompt");

        Assert.False(prompt.IsVisible);

        ConnectTo(shell, "srv-1");

        Assert.True(shell.ViewModel.IsAutoConnectPromptOpen);
        Assert.True(prompt.IsVisible);
        Assert.Equal("Connected to Living Room", shell.Find<TextBlock>("PromptServerLine").Text);
        Assert.True(IsOverTheBody(shell, prompt));
    });

    [Fact]
    public void ThePrompt_DoesNotAppearInAdvertiseMode() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.ConnectionMode = ConnectionMode.AdvertiseOnly);

        ConnectTo(shell, "srv-1");

        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);
        Assert.False(shell.Find<Border>("AutoConnectPrompt").IsVisible);
    });

    [Theory]
    [InlineData(AutoConnectPolicy.JustOnce)]
    [InlineData(AutoConnectPolicy.Always)]
    public void ThePrompt_DoesNotAppearWhenAPolicyIsAlreadySet(AutoConnectPolicy policy) => headless.Run(() =>
    {
        using var shell = Shell.Show(s =>
        {
            Discover(s);
            s.AutoConnect = policy;
        });

        ConnectTo(shell, "srv-1");

        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);
    });

    [Fact]
    public void ThePrompt_DoesNotAppearWithoutAServerId() => headless.Run(() =>
    {
        using var shell = Shell.Show(Discover);

        shell.ViewModel.ApplyConnection(Connected);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);
    });

    [Theory]
    [InlineData("JustOnceButton", AutoConnectPolicy.JustOnce)]
    [InlineData("AlwaysButton", AutoConnectPolicy.Always)]
    [InlineData("NotNowButton", AutoConnectPolicy.Never)]
    public void EachAnswer_WritesThePolicyAndRecordsTheServer(string button, AutoConnectPolicy expected) => headless.Run(() =>
    {
        using var shell = Shell.Show(Discover);

        ConnectTo(shell, "srv-1");
        Assert.True(shell.ViewModel.IsAutoConnectPromptOpen);

        shell.Find<Button>(button).Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);
        Assert.False(shell.Find<Border>("AutoConnectPrompt").IsVisible);
        Assert.Equal(expected, shell.Settings.Current.AutoConnect);
        Assert.Equal("srv-1", shell.Settings.Current.AutoConnectPromptedServerId);

        // The Settings combo shows the answer too, not the value it loaded at startup.
        Assert.Equal(expected, shell.ViewModel.Settings.AutoConnect);

        // The service's own start-up target is untouched by the answer.
        Assert.Equal("srv-1", shell.Settings.Current.LastServerId);
    });

    [Fact]
    public void ThePrompt_IsAskedOncePerServer() => headless.Run(() =>
    {
        using var shell = Shell.Show(Discover);

        ConnectTo(shell, "srv-1");
        shell.Find<Button>("NotNowButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The same server again: policy is still Never, but the question has been asked.
        shell.ViewModel.ApplyConnection(Disconnected);
        ConnectTo(shell, "srv-1");
        Assert.Equal(AutoConnectPolicy.Never, shell.Settings.Current.AutoConnect);
        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);

        // A different server is a new question.
        shell.ViewModel.ApplyConnection(Disconnected);
        ConnectTo(shell, "srv-2");
        Assert.True(shell.ViewModel.IsAutoConnectPromptOpen);
    });

    [Fact]
    public void ThePrompt_ClosesOnDisconnectWithoutAnswering() => headless.Run(() =>
    {
        using var shell = Shell.Show(Discover);

        ConnectTo(shell, "srv-1");
        Assert.True(shell.ViewModel.IsAutoConnectPromptOpen);

        shell.ViewModel.ApplyConnection(Disconnected);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.IsAutoConnectPromptOpen);
        Assert.Null(shell.Settings.Current.AutoConnectPromptedServerId);

        // Not answered, so it is asked again next time.
        ConnectTo(shell, "srv-1");
        Assert.True(shell.ViewModel.IsAutoConnectPromptOpen);
    });

    [Fact]
    public void ThePromptedServer_RoundTripsThroughAClone()
    {
        var settings = new PlayerSettings { AutoConnectPromptedServerId = "srv-9" };

        Assert.Equal("srv-9", settings.Clone().AutoConnectPromptedServerId);
    }

    private static void Discover(PlayerSettings settings) => settings.ConnectionMode = ConnectionMode.DiscoverOnly;

    private static void ConnectTo(Shell shell, string serverId)
    {
        shell.Settings.Update(s => s.LastServerId = serverId);
        shell.ViewModel.ApplyConnection(Connected);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsOverTheBody(Shell shell, Border prompt) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(shell.Find<Panel>("Body")).Contains(prompt);
}
