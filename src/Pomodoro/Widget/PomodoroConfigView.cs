using System.Text.Json;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using Pomodoro.Timer;

namespace Pomodoro.Widget;

/// <summary>The widget's configuration form. Each input's key is the data key it writes.</summary>
internal static class PomodoroConfigView
{
	public const UiWidgetAppearanceFields AppearanceFields = UiWidgetAppearanceFields.BackgroundColor
		| UiWidgetAppearanceFields.AccentColor
		| UiWidgetAppearanceFields.Label
		| UiWidgetAppearanceFields.Border
		| UiWidgetAppearanceFields.TransparentBackground;

	public static UiElement Build(JsonElement data)
	{
		var settings = PomodoroSettings.Parse(data);

		return new UiWidgetConfiguration
		{
			Key = "root",
			Properties = new UiWidgetProperties
			{
				Key = "properties",
				Children =
				[
					new UiHeading { Key = "durations", Text = Strings.Config.Durations() },
					Minutes(PomodoroSettings.FocusMinutesKey, Strings.Config.FocusMinutes(), settings.FocusMinutes),
					Minutes(PomodoroSettings.ShortBreakMinutesKey, Strings.Config.ShortBreakMinutes(),
						settings.ShortBreakMinutes),
					Minutes(PomodoroSettings.LongBreakMinutesKey, Strings.Config.LongBreakMinutes(),
						settings.LongBreakMinutes),
					new UiNumberInput
					{
						Key = PomodoroSettings.RoundsBeforeLongBreakKey,
						Label = Strings.Config.RoundsBeforeLongBreak(),
						Binding = Bind.To(new UiState<double>(settings.RoundsBeforeLongBreak)),
						Min = 1,
						Max = PomodoroSettings.MaxRounds,
						Step = 1
					},
					new UiBooleanInput
					{
						Key = PomodoroSettings.AutoStartNextKey,
						Label = Strings.Config.AutoStartNext(),
						Description = Strings.Config.AutoStartNextHint(),
						Binding = Bind.To(new UiState<bool>(settings.AutoStartNext))
					},
					new UiBooleanInput
					{
						Key = PomodoroSettings.NotifyOnPhaseEndKey,
						Label = Strings.Config.NotifyOnPhaseEnd(),
						Binding = Bind.To(new UiState<bool>(settings.NotifyOnPhaseEnd))
					},
					UiWidgetAppearance.Section(data, AppearanceFields)
				]
			}
		};
	}

	private static UiNumberInput Minutes(string key, UiText label, int value)
	{
		return new UiNumberInput
		{
			Key = key,
			Label = label,
			Binding = Bind.To(new UiState<double>(value)),
			Min = 1,
			Max = PomodoroSettings.MaxMinutes,
			Step = 1
		};
	}
}
