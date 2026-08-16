using System.Text;
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
	/// <c>adb reverse tcp:19223 tcp:19223</c> echoes its allocated port on stdout. Upstream's
	/// <see cref="AdbRunner.ReversePortAsync"/> lets the child inherit this process's console, so that
	/// bare integer landed on the CLI's own stdout ahead of the <c>--json</c> document (breaking
	/// <c>ConvertFrom-Json</c>) and the long-lived adb server daemon kept the inherited handle, so a
	/// caller piping our stdout never saw the pipe close. The override must run adb with both streams
	/// captured. A captured stream is observable here through the failure detail: if the child's
	/// output were inherited rather than captured, no detail would be available to report.
	/// </summary>
	[Fact]
	public async Task ReversePortAsync_CapturesTheChildStdoutRatherThanInheritingTheConsole()
	{
		using var adb = FakeAdb.Create(exitCode: 1, stdout: "19223");
		var runner = new MauiAdbRunner(adb.Path);
		var spec = new AdbPortSpec(AdbProtocol.Tcp, 19223);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => runner.ReversePortAsync("emulator-5554", spec, spec));

		Assert.Contains("19223", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("no output", error.Message, StringComparison.Ordinal);
		Assert.Equal("-s emulator-5554 reverse tcp:19223 tcp:19223", adb.ReadArguments());
	}

	[Fact]
	public async Task ForwardPortAsync_CapturesTheChildStderrRatherThanInheritingTheConsole()
	{
		using var adb = FakeAdb.Create(exitCode: 1, stderr: "cannot bind listener");
		var runner = new MauiAdbRunner(adb.Path);
		var spec = new AdbPortSpec(AdbProtocol.Tcp, 9223);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => runner.ForwardPortAsync("emulator-5554", spec, spec));

		Assert.Contains("cannot bind listener", error.Message, StringComparison.Ordinal);
		Assert.Equal("-s emulator-5554 forward tcp:9223 tcp:9223", adb.ReadArguments());
	}

	[Fact]
	public async Task PortMappingWriters_OnSuccess_PassAdbTheExpectedArgumentsAndDoNotThrow()
	{
		using var adb = FakeAdb.Create(exitCode: 0, stdout: "19223");
		var runner = new MauiAdbRunner(adb.Path);
		var spec = new AdbPortSpec(AdbProtocol.Tcp, 19223);

		await runner.ReversePortAsync("emulator-5554", spec, spec);
		Assert.Equal("-s emulator-5554 reverse tcp:19223 tcp:19223", adb.ReadArguments());

		await runner.RemoveReversePortAsync("emulator-5554", spec);
		Assert.Equal("-s emulator-5554 reverse --remove tcp:19223", adb.ReadArguments());

		await runner.RemoveForwardPortAsync("emulator-5554", spec);
		Assert.Equal("-s emulator-5554 forward --remove tcp:19223", adb.ReadArguments());
	}

	/// <summary>
	/// A throwaway executable that stands in for adb: it records its own arguments, emits the
	/// requested output on each stream, and exits with the requested code.
	/// </summary>
	sealed class FakeAdb : IDisposable
	{
		FakeAdb(string directory, string path, string argumentsPath)
		{
			Directory = directory;
			Path = path;
			ArgumentsPath = argumentsPath;
		}

		string Directory { get; }

		public string Path { get; }

		string ArgumentsPath { get; }

		public static FakeAdb Create(int exitCode, string? stdout = null, string? stderr = null)
		{
			var directory = System.IO.Path.Combine(
				AppContext.BaseDirectory,
				"maui-adb-tests",
				Guid.NewGuid().ToString("n"));
			System.IO.Directory.CreateDirectory(directory);
			var argumentsPath = System.IO.Path.Combine(directory, "args.txt");

			string path;
			if (OperatingSystem.IsWindows())
			{
				path = System.IO.Path.Combine(directory, "adb.cmd");
				var script = new StringBuilder()
					.AppendLine("@echo off")
					.AppendLine($"echo %*> \"{argumentsPath}\"");
				if (!string.IsNullOrEmpty(stdout))
					script.AppendLine($"echo {stdout}");
				if (!string.IsNullOrEmpty(stderr))
					script.AppendLine($"echo {stderr} 1>&2");
				script.AppendLine($"exit /b {exitCode}");
				File.WriteAllText(path, script.ToString());
			}
			else
			{
				path = System.IO.Path.Combine(directory, "adb");
				var script = new StringBuilder()
					.AppendLine("#!/bin/sh")
					.AppendLine($"echo \"$@\" > \"{argumentsPath}\"");
				if (!string.IsNullOrEmpty(stdout))
					script.AppendLine($"echo \"{stdout}\"");
				if (!string.IsNullOrEmpty(stderr))
					script.AppendLine($"echo \"{stderr}\" 1>&2");
				script.AppendLine($"exit {exitCode}");
				File.WriteAllText(path, script.ToString());
				File.SetUnixFileMode(
					path,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}

			return new FakeAdb(directory, path, argumentsPath);
		}

		public string ReadArguments()
			=> File.ReadAllText(ArgumentsPath).Trim();

		public void Dispose()
		{
			try
			{
				System.IO.Directory.Delete(Directory, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

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
