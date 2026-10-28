using System.Text.Json;
using NUnit.Framework;
using Pomodoro.Timer;

namespace Pomodoro.Tests;

[TestFixture]
public sealed class PomodoroSettingsTests
{
	private static PomodoroSettings Parse(string json) => PomodoroSettings.Parse(JsonDocument.Parse(json).RootElement);

	[Test]
	public void Empty_data_reads_as_the_classic_25_5_15_cycle()
	{
		Assert.That(Parse("{}"), Is.EqualTo(PomodoroSettings.Default));
	}

	[Test]
	public void Out_of_range_values_are_clamped()
	{
		var settings = Parse("""{"focusMinutes":0,"longBreakMinutes":9999,"roundsBeforeLongBreak":-3}""");

		Assert.That(settings.FocusMinutes, Is.EqualTo(1));
		Assert.That(settings.LongBreakMinutes, Is.EqualTo(PomodoroSettings.MaxMinutes));
		Assert.That(settings.RoundsBeforeLongBreak, Is.EqualTo(1));
	}

	[Test]
	public void Values_of_the_wrong_type_fall_back_to_the_defaults()
	{
		var settings = Parse("""{"focusMinutes":"fifty","autoStartNext":"yes","label":42}""");

		Assert.That(settings, Is.EqualTo(PomodoroSettings.Default));
	}

	[Test]
	public void The_label_is_trimmed_and_blank_means_none()
	{
		Assert.That(Parse("""{"label":"  Deep work "}""").Label, Is.EqualTo("Deep work"));
		Assert.That(Parse("""{"label":"   "}""").Label, Is.Null);
	}
}
