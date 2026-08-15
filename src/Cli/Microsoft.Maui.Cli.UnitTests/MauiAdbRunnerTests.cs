using Microsoft.Maui.Cli.Providers.Android;
using Xamarin.Android.Tools;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// Regression coverage for the <c>adb reverse --list</c> reader. The upstream
/// <see cref="AdbRunner.ListReversePortsAsync"/> keeps only lines that start with the literal
/// <c>(reverse)</c> prefix older adb builds emitted; modern adb prints the ADB transport id there
/// (<c>host-17</c>), so upstream always returned an empty list. An empty list is indistinguishable
/// from "no mapping", which made the DevFlow broker reverse check fail closed on every Android
/// flow run.
/// </summary>
public class MauiAdbRunnerTests
{
	[Fact]
	public void ParseReverseList_ReadsMappingsWhoseFirstColumnIsATransportIdNotASerial()
	{
		var rules = MauiAdbRunner.ParseReverseList("host-17 tcp:19223 tcp:19223\n");

		var rule = Assert.Single(rules);
		Assert.Equal(AdbProtocol.Tcp, rule.Local.Protocol);
		Assert.Equal(19223, rule.Local.Port);
		Assert.Equal(AdbProtocol.Tcp, rule.Remote.Protocol);
		Assert.Equal(19223, rule.Remote.Port);
	}

	/// <summary>
	/// <c>adb reverse --list</c> prints <c>&lt;transport&gt; &lt;device-side&gt; &lt;host-side&gt;</c>, and the
	/// device side is stored in <see cref="AdbPortRule.Local"/> on purpose: DevFlow's conflict guard
	/// keys on <c>Local.Port</c> and must see the port the broker would rebind on the device. This is
	/// the opposite of upstream's role naming, so the asymmetric case is pinned here rather than left
	/// to the symmetric mappings DevFlow itself creates.
	/// </summary>
	[Fact]
	public void ParseReverseList_StoresTheDeviceSideSpecInLocal()
	{
		var rules = MauiAdbRunner.ParseReverseList("host-3 tcp:41113 tcp:42224");

		var rule = Assert.Single(rules);
		Assert.Equal(41113, rule.Local.Port);
		Assert.Equal(42224, rule.Remote.Port);
	}

	[Fact]
	public void ParseReverseList_ReadsEveryMappingLine()
	{
		var rules = MauiAdbRunner.ParseReverseList(
			"host-17 tcp:19223 tcp:19223\r\nhost-17 tcp:5000 tcp:5001\r\n");

		Assert.Equal(2, rules.Count);
		Assert.Contains(rules, r => r.Local.Port == 19223 && r.Remote.Port == 19223);
		Assert.Contains(rules, r => r.Local.Port == 5000 && r.Remote.Port == 5001);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   \r\n")]
	public void ParseReverseList_WithoutOutput_ReturnsNoMappings(string? output)
		=> Assert.Empty(MauiAdbRunner.ParseReverseList(output));

	/// <summary>
	/// Unparsable lines are dropped rather than guessed at, so a future adb output change degrades
	/// to "mapping not found" (which the caller reports and repairs) instead of a fabricated rule.
	/// </summary>
	[Fact]
	public void ParseReverseList_IgnoresLinesThatAreNotPortMappings()
	{
		var rules = MauiAdbRunner.ParseReverseList(
			"cannot connect to daemon\nhost-17 localabstract:foo localabstract:bar\nhost-17 tcp:19223 tcp:19223");

		var rule = Assert.Single(rules);
		Assert.Equal(19223, rule.Local.Port);
	}
}
