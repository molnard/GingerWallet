using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Extensions;

namespace WalletWasabi.Fluent.Behaviors;

internal class TextBoxAddLineBreakBehavior : AttachedToVisualTreeBehavior<TextBox>
{
	public static readonly StyledProperty<KeyGesture?> KeyGestureProperty =
		AvaloniaProperty.Register<TextBoxAddLineBreakBehavior, KeyGesture?>(nameof(KeyGesture), defaultValue: new KeyGesture(Key.Enter, KeyModifiers.Shift));

	public KeyGesture? KeyGesture
	{
		get => GetValue(KeyGestureProperty);
		set => SetValue(KeyGestureProperty, value);
	}

	protected override void OnAttachedToVisualTree(CompositeDisposable disposable)
	{
		if (AssociatedObject is null)
		{
			return;
		}

		AssociatedObject.OnEvent(InputElement.KeyDownEvent)
						.Where(e => KeyGesture is { } kg && kg.Matches(e.EventArgs))
						.Do(_ =>
						{
							AssociatedObject.AcceptsReturn = true;
							AssociatedObject.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = Environment.NewLine, Source = AssociatedObject, Route = RoutingStrategies.Bubble });
							AssociatedObject.AcceptsReturn = false;
						})
						.Subscribe()
						.DisposeWith(disposable);
	}
}
