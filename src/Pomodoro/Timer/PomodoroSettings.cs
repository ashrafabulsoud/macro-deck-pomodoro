using System.Text.Json;

namespace Pomodoro.Timer;

/// <summary>A widget's timer configuration, read leniently from its stored data.</summary>
public sealed record PomodoroSettings(
	int FocusMinutes,
	int ShortBreakMinutes,
	int LongBreakMinutes,
	int RoundsBeforeLongBreak,
	bool AutoStartNext,
	bool NotifyOnPhaseEnd,
	string? Label)
{
	public const string FocusMinutesKey = "focusMinutes";
	public const string ShortBreakMinutesKey = "shortBreakMinutes";
	public const string LongBreakMinutesKey = "longBreakMinutes";
	public const string RoundsBeforeLongBreakKey = "roundsBeforeLongBreak";
	public const string AutoStartNextKey = "autoStartNext";
	public const string NotifyOnPhaseEndKey = "notifyOnPhaseEnd";

	// Written by Macro Deck's standard appearance field and the Set Label action.
	public const string LabelKey = "label";

	public const int MaxMinutes = 180;
	public const int MaxRounds = 12;

	public static PomodoroSettings Default { get; } = new(25, 5, 15, 4, false, false, null);

	public long DurationMsOf(PomodoroPhase phase)
	{
		var minutes = phase switch
		{
			PomodoroPhase.Focus => FocusMinutes,
			PomodoroPhase.ShortBreak => ShortBreakMinutes,
			_ => LongBreakMinutes
		};

		return minutes * 60_000L;
	}

	public static PomodoroSettings Parse(JsonElement data)
	{
		return new PomodoroSettings(
			ReadInt(data, FocusMinutesKey, Default.FocusMinutes, 1, MaxMinutes),
			ReadInt(data, ShortBreakMinutesKey, Default.ShortBreakMinutes, 1, MaxMinutes),
			ReadInt(data, LongBreakMinutesKey, Default.LongBreakMinutes, 1, MaxMinutes),
			ReadInt(data, RoundsBeforeLongBreakKey, Default.RoundsBeforeLongBreak, 1, MaxRounds),
			ReadBool(data, AutoStartNextKey),
			ReadBool(data, NotifyOnPhaseEndKey),
			ReadLabel(data));
	}

	private static bool ReadBool(JsonElement data, string key)
	{
		return data.ValueKind == JsonValueKind.Object
			&& data.TryGetProperty(key, out var value)
			&& value.ValueKind == JsonValueKind.True;
	}

	private static string? ReadLabel(JsonElement data)
	{
		return data.ValueKind == JsonValueKind.Object
			&& data.TryGetProperty(LabelKey, out var value)
			&& value.ValueKind == JsonValueKind.String
			&& value.GetString() is { } label
			&& !string.IsNullOrWhiteSpace(label)
				? label.Trim()
				: null;
	}

	private static int ReadInt(JsonElement data, string key, int fallback, int min, int max)
	{
		if (data.ValueKind != JsonValueKind.Object
			|| !data.TryGetProperty(key, out var value)
			|| value.ValueKind != JsonValueKind.Number
			|| !value.TryGetDouble(out var number)
			|| !double.IsFinite(number))
		{
			return fallback;
		}

		return (int)Math.Clamp(Math.Round(number), min, max);
	}
}
