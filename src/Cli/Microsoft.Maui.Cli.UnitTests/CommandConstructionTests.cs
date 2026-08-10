using System.CommandLine;
using Microsoft.Maui.Cli.Commands;
using Microsoft.Maui.Cli.DevFlow;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

public class CommandConstructionTests
{
	[Fact]
	public void BuildRootCommand_DoesNotThrow()
	{
		// Verifies all commands and options can be constructed without errors.
		// Catches issues like descriptions passed as aliases (which throws
		// ArgumentException for whitespace in alias names).
		var rootCommand = Program.BuildRootCommand();

		Assert.NotNull(rootCommand);
		Assert.NotEmpty(rootCommand.Subcommands);
	}

	[Fact]
	public void RootCommand_IncludesProjectVersionCommands()
	{
		var rootCommand = Program.BuildRootCommand();

		var projectCommand = Assert.Single(rootCommand.Subcommands, command => command.Name == "project");
		var versionCommand = Assert.Single(projectCommand.Subcommands, command => command.Name == "version");
		Assert.Contains(versionCommand.Subcommands, command => command.Name == "show");
		Assert.Contains(versionCommand.Subcommands, command => command.Name == "list");
		Assert.Contains(versionCommand.Subcommands, command => command.Name == "set");
		Assert.Contains(versionCommand.Subcommands, command => command.Name == "use-workload");

		var showCommand = Assert.Single(versionCommand.Subcommands, command => command.Name == "show");
		Assert.Contains("check", showCommand.Aliases);
	}

	[Fact]
	public void VersionCommand_RemainsCliVersionCommand()
	{
		var rootCommand = Program.BuildRootCommand();

		var versionCommand = Assert.Single(rootCommand.Subcommands, command => command.Name == "version");
		Assert.Empty(versionCommand.Subcommands);
	}

	[Fact]
	public void DevFlowCommand_AllOptionsHaveValidAliases()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		// Recursively verify every option in the command tree has no whitespace in aliases
		AssertNoWhitespaceAliases(devflowCommand);
	}

	[Fact]
	public void DevFlowCommand_UsesMcpAsPrimaryCommandName()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var mcpCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "mcp");
		Assert.Contains("mcp-serve", mcpCommand.Aliases);
	}

	[Fact]
	public void DevFlowCommand_IncludesInitAndSkillsCommands()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var initCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "init");
		Assert.Contains("onboard", initCommand.Aliases);

		var skillsCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "skills");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "install");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "list");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "check");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "update");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "remove");
		Assert.Contains(skillsCommand.Subcommands, c => c.Name == "doctor");
	}

	[Fact]
	public void DevFlowCommand_IncludesThemeCommands()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var themeCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "theme");
		Assert.Contains(themeCommand.Subcommands, c => c.Name == "get");
		var setCommand = Assert.Single(themeCommand.Subcommands, c => c.Name == "set");
		var scopeOption = (Option<string>)Assert.Single(setCommand.Options, option => option.Name == "--scope");
		var parseResult = themeCommand.Parse("set dark");

		Assert.Empty(parseResult.Errors);
		Assert.Equal("auto", parseResult.GetValue(scopeOption));
	}

	[Fact]
	public void DevFlowCommand_IncludesDiagnosticsCommands()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var diagnostics = Assert.Single(devflowCommand.Subcommands, c => c.Name == "diagnostics");

		var layout = Assert.Single(diagnostics.Subcommands, c => c.Name == "layout");
		Assert.Contains(layout.Options, option => option.Name == "--element");
		Assert.Contains(layout.Options, option => option.Name == "--max-elements");
		Assert.Empty(layout.Parse("--element MyList --max-elements 500").Errors);

		var performance = Assert.Single(diagnostics.Subcommands, c => c.Name == "performance");
		var durationOption = (Option<int>)Assert.Single(performance.Options, option => option.Name == "--duration");
		Assert.Contains(performance.Options, option => option.Name == "--sample-interval");
		Assert.Contains(performance.Options, option => option.Name == "--attach");

		var parseResult = performance.Parse("--duration 12");
		Assert.Empty(parseResult.Errors);
		Assert.Equal(12, parseResult.GetValue(durationOption));
		Assert.Equal(5, performance.Parse("").GetValue(durationOption));
	}

	[Fact]
	public void UiTree_DefaultsToActiveVisualProjection()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);
		var uiCommand = Assert.Single(devflowCommand.Subcommands, command => command.Name == "ui");
		var treeCommand = Assert.Single(uiCommand.Subcommands, command => command.Name == "tree");
		var projectionOption = (Option<string>)Assert.Single(
			treeCommand.Options,
			option => option.Name == "--projection");

		var parseResult = treeCommand.Parse("");

		Assert.Empty(parseResult.Errors);
		Assert.Equal("activeVisual", parseResult.GetValue(projectionOption));
	}

	[Fact]
	public void FlowReplay_BareEvidenceOnFailureOptionIsPresent()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);
		var flow = Assert.Single(devflowCommand.Subcommands, command => command.Name == "flow");
		var replay = Assert.Single(flow.Subcommands, command => command.Name == "replay");
		var evidence = (Option<string?>)Assert.Single(
			replay.Options,
			option => option.Name == "--evidence-on-failure");

		var omitted = replay.Parse("scenario.md");
		var bare = replay.Parse("scenario.md --evidence-on-failure");
		var valued = replay.Parse("scenario.md --evidence-on-failure failure.mauitrace");

		Assert.Null(omitted.GetResult(evidence));
		Assert.NotNull(bare.GetResult(evidence));
		Assert.Empty(bare.GetResult(evidence)!.Tokens);
		Assert.Equal("failure.mauitrace", valued.GetValue(evidence));
	}

	[Fact]
	public void FlowValidateCommand_IsPresentAndAcceptsAFlowPath()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);
		var flow = Assert.Single(devflowCommand.Subcommands, command => command.Name == "flow");
		var validate = Assert.Single(flow.Subcommands, command => command.Name == "validate");

		Assert.Empty(validate.Parse("scenario.md").Errors);
	}

	[Fact]
	public void DevFlowCommand_UpdateSkillIsHiddenCompatibilityAliasForSkillsUpdate()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var updateSkillCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "update-skill");
		Assert.True(updateSkillCommand.Hidden);
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--scope");
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--target");
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--path");
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--force");
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--allow-downgrade");
		Assert.Contains(updateSkillCommand.Options, option => option.Name == "--interactive");
		Assert.DoesNotContain(updateSkillCommand.Options, option => option.Name == "--branch");
		Assert.DoesNotContain(updateSkillCommand.Options, option => option.Name == "--output");
	}

	[Fact]
	public void DevFlowCommand_TargetOptionsDefaultToAuto()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);

		var initCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "init");
		AssertTargetOptionDefault(initCommand, "init");

		var skillsCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "skills");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "install"), "install");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "list"), "list");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "check"), "check");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "update"), "update");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "doctor"), "doctor");
		AssertTargetOptionDefault(Assert.Single(skillsCommand.Subcommands, c => c.Name == "remove"), "remove maui-devflow-onboard");

		AssertTargetOptionDefault(Assert.Single(devflowCommand.Subcommands, c => c.Name == "update-skill"), "update-skill");
	}

	[Fact]
	public void DevFlowCommand_InvalidSkillScopeAndTargetFailDuringParsing()
	{
		var jsonOption = new Option<bool>("--json");
		var devflowCommand = DevFlowCommands.CreateDevFlowCommand(jsonOption);
		var initCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "init");
		var skillsCommand = Assert.Single(devflowCommand.Subcommands, c => c.Name == "skills");
		var updateCommand = Assert.Single(skillsCommand.Subcommands, c => c.Name == "update");

		Assert.NotEmpty(initCommand.Parse("init --target bogus").Errors);
		Assert.NotEmpty(initCommand.Parse("init --scope all").Errors);
		Assert.Empty(updateCommand.Parse("update --scope all").Errors);
		Assert.NotEmpty(updateCommand.Parse("update --scope bogus").Errors);
	}

	private static void AssertNoWhitespaceAliases(Command command)
	{
		foreach (var option in command.Options)
		{
			Assert.False(option.Name.Any(char.IsWhiteSpace), $"Option name contains whitespace: \"{option.Name}\" in command '{command.Name}'");
			foreach (var alias in option.Aliases)
			{
				Assert.False(alias.Any(char.IsWhiteSpace), $"Option alias contains whitespace: \"{alias}\" in command '{command.Name}'");
			}
		}

		foreach (var subcommand in command.Subcommands)
		{
			AssertNoWhitespaceAliases(subcommand);
		}
	}

	private static void AssertTargetOptionDefault(Command command, string commandLine)
	{
		var targetOption = (Option<string>)Assert.Single(command.Options, option => option.Name == "--target");
		var parseResult = command.Parse(commandLine);

		Assert.Empty(parseResult.Errors);
		Assert.Equal("auto", parseResult.GetValue(targetOption));
	}
}
