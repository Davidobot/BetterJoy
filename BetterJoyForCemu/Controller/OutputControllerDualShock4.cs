using System;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace BetterJoyForCemu.Controller {
	public enum DpadDirection {
		None,
		Northwest,
		West,
		Southwest,
		South,
		Southeast,
		East,
		Northeast,
		North,
	}

	public struct OutputControllerDualShock4InputState {
		public bool triangle;
		public bool circle;
		public bool cross;
		public bool square;

		public bool trigger_left;
		public bool trigger_right;

		public bool shoulder_left;
		public bool shoulder_right;

		public bool options;
		public bool share;
		public bool ps;
		public bool touchpad;

		public bool thumb_left;
		public bool thumb_right;

		public DpadDirection dPad;

		public byte thumb_left_x;
		public byte thumb_left_y;
		public byte thumb_right_x;
		public byte thumb_right_y;

		public byte trigger_left_value;
		public byte trigger_right_value;

		public bool IsEqual(OutputControllerDualShock4InputState other) {
			bool buttons = triangle == other.triangle
				&& circle == other.circle
				&& cross == other.cross
				&& square == other.square
				&& trigger_left == other.trigger_left
				&& trigger_right == other.trigger_right
				&& shoulder_left == other.shoulder_left
				&& shoulder_right == other.shoulder_right
				&& options == other.options
				&& share == other.share
				&& ps == other.ps
				&& touchpad == other.touchpad
				&& thumb_left == other.thumb_left
				&& thumb_right == other.thumb_right
				&& dPad == other.dPad;

			bool axis = thumb_left_x == other.thumb_left_x
				&& thumb_left_y == other.thumb_left_y
				&& thumb_right_x == other.thumb_right_x
				&& thumb_right_y == other.thumb_right_y;

			bool triggers = trigger_left_value == other.trigger_left_value
				&& trigger_right_value == other.trigger_right_value;

			return buttons && axis && triggers;
		}
	}

	public class OutputControllerDualShock4 {
		private IDualShock4Controller controller;

		private OutputControllerDualShock4InputState current_state;


		public delegate void DualShock4FeedbackReceivedEventHandler(DualShock4FeedbackReceivedEventArgs e);
		public event DualShock4FeedbackReceivedEventHandler FeedbackReceived;

		public OutputControllerDualShock4() {
			controller = Program.emClient.CreateDualShock4Controller();
			Init();
		}

		public OutputControllerDualShock4(ushort vendor_id, ushort product_id) {
			controller = Program.emClient.CreateDualShock4Controller(vendor_id, product_id);
			Init();
		}

		private void Init() {
			controller.AutoSubmitReport = false;
			controller.FeedbackReceived += FeedbackReceivedRcv;
		}

		private void FeedbackReceivedRcv(object _sender, DualShock4FeedbackReceivedEventArgs e) {
			FeedbackReceived(e);
		}

		public void Connect() {
			controller.Connect();
		}

		public void Disconnect() {
			controller.Disconnect();
		}

		public bool UpdateInput(OutputControllerDualShock4InputState new_state) {
			if (current_state.IsEqual(new_state)) {
				return false;
			}

			DoUpdateInput(new_state);

			return true;
		}

		private void DoUpdateInput(OutputControllerDualShock4InputState new_state) {
			controller.SetButtonState(DualShock4Button.Triangle, new_state.triangle);
			controller.SetButtonState(DualShock4Button.Circle, new_state.circle);
			controller.SetButtonState(DualShock4Button.Cross, new_state.cross);
			controller.SetButtonState(DualShock4Button.Square, new_state.square);

			controller.SetButtonState(DualShock4Button.ShoulderLeft, new_state.shoulder_left);
			controller.SetButtonState(DualShock4Button.ShoulderRight, new_state.shoulder_right);

			controller.SetButtonState(DualShock4Button.TriggerLeft, new_state.trigger_left);
			controller.SetButtonState(DualShock4Button.TriggerRight, new_state.trigger_right);

			controller.SetButtonState(DualShock4Button.ThumbLeft, new_state.thumb_left);
			controller.SetButtonState(DualShock4Button.ThumbRight, new_state.thumb_right);

			controller.SetButtonState(DualShock4Button.Share, new_state.share);
			controller.SetButtonState(DualShock4Button.Options, new_state.options);
			controller.SetButtonState(DualShock4SpecialButton.Ps, new_state.ps);
			controller.SetButtonState(DualShock4SpecialButton.Touchpad, new_state.touchpad);

			controller.SetDPadDirection(MapDPadDirection(new_state.dPad));

			controller.SetAxisValue(DualShock4Axis.LeftThumbX, new_state.thumb_left_x);
			controller.SetAxisValue(DualShock4Axis.LeftThumbY, new_state.thumb_left_y);
			controller.SetAxisValue(DualShock4Axis.RightThumbX, new_state.thumb_right_x);
			controller.SetAxisValue(DualShock4Axis.RightThumbY, new_state.thumb_right_y);

			controller.SetSliderValue(DualShock4Slider.LeftTrigger, new_state.trigger_left_value);
			controller.SetSliderValue(DualShock4Slider.RightTrigger, new_state.trigger_right_value);

			controller.SubmitReport();

			current_state = new_state;
		}

		private DualShock4DPadDirection MapDPadDirection(DpadDirection dPad) {
			switch (dPad) {
				case DpadDirection.None: return DualShock4DPadDirection.None;
				case DpadDirection.North: return DualShock4DPadDirection.North;
				case DpadDirection.Northeast: return DualShock4DPadDirection.Northeast;
				case DpadDirection.East: return DualShock4DPadDirection.East;
				case DpadDirection.Southeast: return DualShock4DPadDirection.Southeast;
				case DpadDirection.South: return DualShock4DPadDirection.South;
				case DpadDirection.Southwest: return DualShock4DPadDirection.Southwest;
				case DpadDirection.West: return DualShock4DPadDirection.West;
				case DpadDirection.Northwest: return DualShock4DPadDirection.Northwest;
				default: throw new NotImplementedException();
			}
		}
	}
}
