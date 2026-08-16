// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Maui.Cli.Utils;
using Xamarin.Android.Tools;

namespace Microsoft.Maui.Cli.Providers.Android;

/// <summary>
/// <see cref="AdbRunner"/> with a corrected <c>adb reverse --list</c> reader.
/// </summary>
/// <remarks>
/// <para>
/// The upstream <see cref="AdbRunner.ListReversePortsAsync"/> keeps only the lines that begin with
/// the literal <c>(reverse)</c> prefix that older adb builds emitted. Modern adb prints the ADB
/// transport id instead (<c>host-17 tcp:5000 tcp:5000</c>), so every line is discarded and the
/// list always comes back empty. Verified against Xamarin.Android.Tools.AndroidSdk
/// 1.0.189-preview.58: <c>ParseReverseListOutput</c> takes no serial at all, and feeding it
/// <c>host-17 tcp:5000 tcp:5000</c> returns zero rules while <c>(reverse) tcp:5000 tcp:5000</c>
/// returns one.
/// </para>
/// <para>
/// An empty reverse list is indistinguishable from "no mapping present", which made every
/// reverse-mapping check fail closed (the DevFlow broker reverse could never be verified) and
/// made the foreign-mapping conflict guard unreachable. Reading the raw output here keeps both
/// checks real.
/// </para>
/// <para>
/// The port-mapping writers are overridden for a different upstream defect: they let the adb
/// child process inherit this process's console, so <c>adb reverse tcp:19223 tcp:19223</c> echoes
/// its allocated port straight onto our stdout. That corrupts <c>--json</c> output with a bare
/// integer before the JSON document, and the adb server daemon adb forks on first use inherits
/// the same handle for its whole lifetime, so a caller piping our stdout never sees the pipe
/// close. Running adb through <see cref="ProcessRunner"/> captures both streams instead.
/// </para>
/// </remarks>
internal sealed class MauiAdbRunner : AdbRunner
{
	static readonly char[] ColumnSeparators = [' ', '\t', '\r'];

	readonly string _adbPath;
	readonly Dictionary<string, string>? _environmentVariables;

	public MauiAdbRunner(string adbPath, IDictionary<string, string>? environmentVariables = null)
		: base(adbPath, environmentVariables)
	{
		_adbPath = adbPath;
		_environmentVariables = environmentVariables is null
			? null
			: new Dictionary<string, string>(environmentVariables);
	}

	public override async Task<IReadOnlyList<AdbPortRule>> ListReversePortsAsync(
		string serial,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serial);

		var result = await ProcessRunner.RunAsync(
			_adbPath,
			["-s", serial, "reverse", "--list"],
			environmentVariables: _environmentVariables,
			cancellationToken: cancellationToken);

		if (!result.Success)
		{
			var detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
			throw new InvalidOperationException(
				$"`adb reverse --list` exited with code {result.ExitCode}: {detail}");
		}

		return ParseReverseList(result.StandardOutput);
	}

	public override Task ReversePortAsync(
		string serial,
		AdbPortSpec remote,
		AdbPortSpec local,
		CancellationToken cancellationToken = default)
		=> RunPortCommandAsync("reverse", serial, remote, local, cancellationToken);

	public override Task ForwardPortAsync(
		string serial,
		AdbPortSpec local,
		AdbPortSpec remote,
		CancellationToken cancellationToken = default)
		=> RunPortCommandAsync("forward", serial, local, remote, cancellationToken);

	public override Task RemoveReversePortAsync(
		string serial,
		AdbPortSpec remote,
		CancellationToken cancellationToken = default)
		=> RunPortRemovalAsync("reverse", serial, remote, cancellationToken);

	public override Task RemoveForwardPortAsync(
		string serial,
		AdbPortSpec local,
		CancellationToken cancellationToken = default)
		=> RunPortRemovalAsync("forward", serial, local, cancellationToken);

	/// <summary>
	/// Runs an <c>adb reverse</c>/<c>adb forward</c> mapping command with both child streams
	/// captured, so adb's own port echo never reaches this process's stdout.
	/// </summary>
	async Task RunPortCommandAsync(
		string direction,
		string serial,
		AdbPortSpec first,
		AdbPortSpec second,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serial);
		ArgumentNullException.ThrowIfNull(first);
		ArgumentNullException.ThrowIfNull(second);

		await RunCapturedAsync(
			["-s", serial, direction, first.ToSocketSpec(), second.ToSocketSpec()],
			$"`adb {direction} {first.ToSocketSpec()} {second.ToSocketSpec()}`",
			cancellationToken).ConfigureAwait(false);
	}

	async Task RunPortRemovalAsync(
		string direction,
		string serial,
		AdbPortSpec spec,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serial);
		ArgumentNullException.ThrowIfNull(spec);

		await RunCapturedAsync(
			["-s", serial, direction, "--remove", spec.ToSocketSpec()],
			$"`adb {direction} --remove {spec.ToSocketSpec()}`",
			cancellationToken).ConfigureAwait(false);
	}

	async Task RunCapturedAsync(string[] arguments, string description, CancellationToken cancellationToken)
	{
		var result = await ProcessRunner.RunAsync(
			_adbPath,
			arguments,
			environmentVariables: _environmentVariables,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		if (!result.Success)
		{
			var detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? "no output";
			throw new InvalidOperationException($"{description} exited with code {result.ExitCode}: {detail}");
		}
	}

	/// <summary>
	/// Parses <c>adb reverse --list</c> output. Each mapping line is
	/// <c>&lt;transport&gt; &lt;device-side spec&gt; &lt;host-side spec&gt;</c>; the two port specs are
	/// read from the end of the line so an extra leading column never drops a mapping. Lines whose
	/// last two columns are not port specs are ignored rather than guessed at.
	/// </summary>
	/// <remarks>
	/// The device-side spec is deliberately stored in <see cref="AdbPortRule.Local"/>, which is the
	/// opposite of the role names upstream uses for the same struct (upstream follows adb's own
	/// <c>reverse REMOTE LOCAL</c> argument order, where REMOTE is the device side). DevFlow's
	/// conflict guard, <c>AndroidDevFlowPortForwarder.FindForeignLocalMapping</c>, keys on
	/// <c>Local.Port</c> and must see the device-side port, because that is the port the broker
	/// would rebind. Do not feed these rules back into <see cref="AdbRunner.ReversePortAsync"/> or
	/// <see cref="AdbRunner.RemoveReversePortAsync"/> without swapping the roles back: those take
	/// the device-side spec as <c>remote</c>.
	/// </remarks>
	internal static IReadOnlyList<AdbPortRule> ParseReverseList(string? output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return [];

		var rules = new List<AdbPortRule>();
		foreach (var line in output.Split('\n'))
		{
			var columns = line.Split(ColumnSeparators, StringSplitOptions.RemoveEmptyEntries);
			if (columns.Length < 3)
				continue;

			var deviceSide = AdbPortSpec.TryParse(columns[^2]);
			var hostSide = AdbPortSpec.TryParse(columns[^1]);
			if (deviceSide is null || hostSide is null)
				continue;

			rules.Add(new AdbPortRule(Remote: hostSide, Local: deviceSide));
		}

		return rules;
	}

	static string? FirstLine(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;

		var newline = value.IndexOfAny(['\r', '\n']);
		var line = (newline < 0 ? value : value[..newline]).Trim();
		return line.Length == 0 ? null : line;
	}
}
