using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Pomodoro.Timer;

namespace Pomodoro.Widget;

internal static class PomodoroWidgetView
{
	// Tomato red for focus, a calm green for breaks; both read on light and dark tiles.
	public const string FocusColor = "#e5533d";
	public const string BreakColor = "#3fa66a";
	private const string PausedColor = "#8a8a8a";
	private const string AlertTextColor = "#ffffff";

	public static UiElement Build(
		UiState<PomodoroFace> face,
		UiWidgetAppearanceValues appearance,
		int cornerRadius,
		IReadOnlyList<UiEventHandler>? events = null)
	{
		ArgumentNullException.ThrowIfNull(face);
		ArgumentNullException.ThrowIfNull(appearance);

		var safeArea = UiWidgetSafeArea.For(cornerRadius);
		var focusColor = appearance.AccentColor ?? FocusColor;

		return new UiStack
		{
			Key = "pomodoro",
			Fill = true,
			Background = appearance.BackgroundColor is { } background ? UiValue.Of(background) : UiValue.None<string>(),
			Events = events ?? [],
			Children =
			[
				new UiWhen
				{
					Key = "finishedGate",
					Condition = () => face.Value.IsFinished,
					Content = () => Finished(face, focusColor, safeArea)
				},
				new UiWhen
				{
					Key = "clockGate",
					Condition = () => !face.Value.IsFinished,
					Content = () => new UiLayer
					{
						Key = "clockLayer",
						Fill = true,
						Children = [Ring(face, focusColor, safeArea), Readout(face, safeArea)]
					}
				}
			]
		};
	}

	private static UiStack Ring(UiState<PomodoroFace> face, string focusColor, UiSize safeArea)
	{
		return new UiStack
		{
			Key = "ringFrame",
			Fill = true,
			Padding = safeArea,
			Children =
			[
				new UiGauge
				{
					Key = "ring",
					Fill = true,
					Thickness = 0.07,
					StartAngle = 0d,
					EndAngle = 360d,
					Level = UiValue.From(() => face.Value.Level),
					LevelColor = UiValue.From(() => face.Value.IsPaused
						? PausedColor
						: ColorOf(face.Value.Phase, focusColor)),
					Fallback = new UiRangeBar
					{
						Key = "ringFallback",
						Thickness = 0.05,
						Start = 0d,
						End = UiValue.From(() => face.Value.Level)
					}
				}
			]
		};
	}

	private static UiStack Readout(UiState<PomodoroFace> face, UiSize safeArea)
	{
		// The column is as wide as the inside of the ring, so a long label ellipsizes instead of crossing it.
		var column = new UiStack
		{
			Key = "readoutColumn",
			MainSize = UiSize.FromBasis(0.52),
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Gap = 0.015,
			Children =
			[
				Caption("phase", UiText.FromLocalized(() => PhaseName(face.Value.Phase)), UiComponentTextWeights.SemiBold),
				new UiProgressText
				{
					Key = "time",
					Value = UiValue.From(() => face.Value.Progress),
					Format = UiProgressFormats.Remaining,
					Size = 0.19,
					MinSize = 0.09,
					Weight = UiComponentTextWeights.Bold,
					Role = UiValue.From(() => face.Value.IsPaused
						? UiComponentTextRoles.Muted
						: UiComponentTextRoles.Primary),
					Align = UiComponentAlignments.Center,
					Fallback = new UiTextRun
					{
						Key = "timeFallback",
						Text = UiText.From(() => face.Value.StaticTime),
						Size = 0.19,
						MinSize = 0.09,
						Weight = UiComponentTextWeights.Bold,
						Align = UiComponentAlignments.Center
					}
				},
				Caption("round", UiText.FromLocalized(() => Strings.Face.Round(face.Value.Round, face.Value.Rounds)),
					UiComponentTextWeights.Medium),
				new UiWhen
				{
					Key = "labelGate",
					Condition = () => face.Value.Label is not null,
					Content = () => Caption("label", UiText.From(() => face.Value.Label), UiComponentTextWeights.SemiBold)
				}
			]
		};

		return new UiStack
		{
			Key = "readout",
			Fill = true,
			Padding = safeArea,
			Direction = UiComponentDirections.Horizontal,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Children = [column]
		};
	}

	private static UiButton Finished(UiState<PomodoroFace> face, string focusColor, UiSize safeArea)
	{
		var focusDone = face.Value.Phase == PomodoroPhase.Focus;
		var children = new List<UiElement>
		{
			Alert("finishedTitle",
				focusDone ? Strings.Face.FocusDone() : Strings.Face.BreakDone(),
				0.12,
				UiComponentTextWeights.Bold)
		};

		if (focusDone)
		{
			children.Add(Alert("completedToday", Strings.Face.CompletedToday(face.Value.CompletedToday), 0.075,
				UiComponentTextWeights.SemiBold));
		}

		children.Add(Alert("finishedHint", focusDone ? Strings.Face.TapForBreak() : Strings.Face.TapForFocus(), 0.07,
			UiComponentTextWeights.Medium));

		return new UiButton
		{
			Key = "finished",
			Fill = true,
			Corner = UiComponentButtonCorners.Tile,
			// The colour of what comes next, so the tile already says "break" or "focus" before it is read.
			Background = focusDone ? BreakColor : focusColor,
			BorderStyle = UiComponentBorderStyles.Heartbeat,
			BorderColor = AlertTextColor,
			Padding = safeArea,
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Gap = 0.03,
			Children = children
		};
	}

	private static UiTextRun Caption(string key, UiText text, string weight)
	{
		return new UiTextRun
		{
			Key = key,
			Text = text,
			Size = 0.07,
			MinSize = 0.05,
			Weight = weight,
			Role = UiComponentTextRoles.Muted,
			Align = UiComponentAlignments.Center,
			MaxLines = 1
		};
	}

	private static UiTextRun Alert(string key, LocalizedString text, double size, string weight)
	{
		return new UiTextRun
		{
			Key = key,
			Text = text,
			Size = size,
			MinSize = size * 0.65,
			Weight = weight,
			Color = AlertTextColor,
			Align = UiComponentAlignments.Center,
			MaxLines = 2
		};
	}

	internal static string ColorOf(PomodoroPhase phase, string focusColor)
	{
		return phase == PomodoroPhase.Focus ? focusColor : BreakColor;
	}

	internal static LocalizedString PhaseName(PomodoroPhase phase)
	{
		return phase switch
		{
			PomodoroPhase.Focus => Strings.Phase.Focus(),
			PomodoroPhase.ShortBreak => Strings.Phase.ShortBreak(),
			_ => Strings.Phase.LongBreak()
		};
	}
}
