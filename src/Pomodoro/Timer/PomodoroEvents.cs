using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;

namespace Pomodoro.Timer;

/// <summary>The events users can trigger flows from, for example to turn on Focus mode when a focus phase starts.</summary>
public static class PomodoroEvents
{
	// Persisted in users' triggers: never rename.
	public const string PhaseStartedId = "phase-started";
	public const string PhaseFinishedId = "phase-finished";
	public const string PomodoroCompletedId = "pomodoro-completed";

	public const string PhaseParameter = "phase";
	public const string RoundParameter = "round";
	public const string CompletedTodayParameter = "completedToday";
	public const string WidgetIdParameter = "widgetId";
	public const string LabelParameter = "label";

	public static IReadOnlyList<EventDefinition> Definitions { get; } =
	[
		PhaseEvent(PhaseStartedId, Strings.Events.PhaseStarted.Name(), Strings.Events.PhaseStarted.Description()),
		PhaseEvent(PhaseFinishedId, Strings.Events.PhaseFinished.Name(), Strings.Events.PhaseFinished.Description()),
		new()
		{
			Id = PomodoroCompletedId,
			Name = Strings.Events.PomodoroCompleted.Name(),
			Description = Strings.Events.PomodoroCompleted.Description(),
			Category = Strings.Events.Category(),
			ConfigurationParameters = [LabelFilter()],
			PayloadParameters =
			[
				ActionParameter.Number(CompletedTodayParameter, label: Strings.Events.Parameters.CompletedToday()),
				.. WidgetPayload()
			]
		}
	];

	public static void Publish(IEventPublisher publisher, PomodoroTransitionEventArgs args)
	{
		ArgumentNullException.ThrowIfNull(publisher);
		ArgumentNullException.ThrowIfNull(args);

		var transition = args.Transition;
		var payload = new Dictionary<string, object?>
		{
			[WidgetIdParameter] = args.WidgetId.ToString(),
			[LabelParameter] = args.Settings.Label ?? string.Empty
		};

		if (transition.Kind == PomodoroTransitionKind.PomodoroCompleted)
		{
			payload[CompletedTodayParameter] = transition.CompletedToday;
			publisher.Publish(PomodoroCompletedId, payload);
			return;
		}

		payload[PhaseParameter] = PomodoroPhases.IdOf(transition.Phase);
		payload[RoundParameter] = transition.Round;
		publisher.Publish(transition.Kind == PomodoroTransitionKind.PhaseStarted ? PhaseStartedId : PhaseFinishedId,
			payload);
	}

	private static EventDefinition PhaseEvent(string id, MacroDeck.Localization.LocalizedText name,
		MacroDeck.Localization.LocalizedText description)
	{
		return new EventDefinition
		{
			Id = id,
			Name = name,
			Description = description,
			Category = Strings.Events.Category(),
			ConfigurationParameters =
			[
				ActionParameter.Choice(PhaseParameter, PhaseOptions(), label: Strings.Events.Parameters.Phase(),
					description: Strings.Events.Parameters.AnyPhase()),
				LabelFilter()
			],
			PayloadParameters =
			[
				ActionParameter.Choice(PhaseParameter, PhaseOptions(), label: Strings.Events.Parameters.Phase()),
				ActionParameter.Number(RoundParameter, label: Strings.Events.Parameters.Round()),
				.. WidgetPayload()
			]
		};
	}

	// Shares its name with the payload key, so the host only fires the trigger for a widget with that label.
	private static ActionParameter LabelFilter()
	{
		return ActionParameter.Text(LabelParameter, label: Strings.Events.Parameters.WidgetLabel(),
			placeholder: Strings.Events.Parameters.AnyWidget());
	}

	private static IReadOnlyList<ActionParameter> WidgetPayload()
	{
		return
		[
			ActionParameter.Text(LabelParameter, label: Strings.Events.Parameters.WidgetLabel()),
			ActionParameter.Text(WidgetIdParameter, label: Strings.Events.Parameters.WidgetId())
		];
	}

	private static IReadOnlyList<ActionParameterOption> PhaseOptions()
	{
		return
		[
			new ActionParameterOption { Value = PomodoroPhases.FocusId, Label = Strings.Phase.Focus() },
			new ActionParameterOption { Value = PomodoroPhases.ShortBreakId, Label = Strings.Phase.ShortBreak() },
			new ActionParameterOption { Value = PomodoroPhases.LongBreakId, Label = Strings.Phase.LongBreak() }
		];
	}
}
