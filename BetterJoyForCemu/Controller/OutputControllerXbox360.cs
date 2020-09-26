using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu.Controller {
	public struct OutputControllerXbox360InputState {
		// buttons
		public bool thumb_stick_left;
		public bool thumb_stick_right;

		public bool y;
		public bool x;
		public bool b;
		public bool a;

		public bool start;
		public bool back;

		public bool guide;

		public bool shoulder_left;
		public bool shoulder_right;

		// dpad
		public bool dpad_up;
		public bool dpad_right;
		public bool dpad_down;
		public bool dpad_left;

		// axis
		public short axis_left_x;
		public short axis_left_y;

		public short axis_right_x;
		public short axis_right_y;

		// triggers
		public byte trigger_left;
		public byte trigger_right;

		public bool IsEqual(OutputControllerXbox360InputState other) {
			bool buttons = thumb_stick_left == other.thumb_stick_left
				&& thumb_stick_right == other.thumb_stick_right
				&& y == other.y
				&& x == other.x
				&& b == other.b
				&& a == other.a
				&& start == other.start
				&& back == other.back
				&& guide == other.guide
				&& shoulder_left == other.shoulder_left
				&& shoulder_right == other.shoulder_right;

			bool dpad = dpad_up == other.dpad_up
				&& dpad_right == other.dpad_right
				&& dpad_down == other.dpad_down
				&& dpad_left == other.dpad_left;

			bool axis = axis_left_x == other.axis_left_x
				&& axis_left_y == other.axis_left_y
				&& axis_right_x == other.axis_right_x
				&& axis_right_y == other.axis_right_y;

			bool triggers = trigger_left == other.trigger_left
				&& trigger_right == other.trigger_right;

			return buttons && dpad && axis && triggers;
		}
	}

	public class OutputControllerXbox360 {
		private IXbox360Controller xbox_controller;
		private OutputControllerXbox360InputState current_state;

		public delegate void Xbox360FeedbackReceivedEventHandler(Xbox360FeedbackReceivedEventArgs e);

		public event Xbox360FeedbackReceivedEventHandler FeedbackReceived;

		public OutputControllerXbox360() {
			xbox_controller = Program.emClient.CreateXbox360Controller();
			Init();
		}

		public OutputControllerXbox360(ushort vendor_id, ushort product_id) {
			xbox_controller = Program.emClient.CreateXbox360Controller(vendor_id, product_id);
			Init();
		}

		private void Init() {
			xbox_controller.FeedbackReceived += FeedbackReceivedRcv;
			xbox_controller.AutoSubmitReport = false;
		}

		private void FeedbackReceivedRcv(object _sender, Xbox360FeedbackReceivedEventArgs e) {
			FeedbackReceived(e);
		}

		public bool UpdateInput(OutputControllerXbox360InputState new_state) {
			if (current_state.IsEqual(new_state)) {
				return false;
			}

			DoUpdateInput(new_state);

			return true;
		}

		public void Connect() {
			xbox_controller.Connect();
			DoUpdateInput(new OutputControllerXbox360InputState());
		}

		public void Disconnect() {
			xbox_controller.Disconnect();
		}

		private void DoUpdateInput(OutputControllerXbox360InputState new_state) {
			xbox_controller.SetButtonState(Xbox360Button.LeftThumb, new_state.thumb_stick_left);
			xbox_controller.SetButtonState(Xbox360Button.RightThumb, new_state.thumb_stick_right);

			xbox_controller.SetButtonState(Xbox360Button.Y, new_state.y);
			xbox_controller.SetButtonState(Xbox360Button.X, new_state.x);
			xbox_controller.SetButtonState(Xbox360Button.B, new_state.b);
			xbox_controller.SetButtonState(Xbox360Button.A, new_state.a);

			xbox_controller.SetButtonState(Xbox360Button.Start, new_state.start);
			xbox_controller.SetButtonState(Xbox360Button.Back, new_state.back);
			xbox_controller.SetButtonState(Xbox360Button.Guide, new_state.guide);

			xbox_controller.SetButtonState(Xbox360Button.Up, new_state.dpad_up);
			xbox_controller.SetButtonState(Xbox360Button.Right, new_state.dpad_right);
			xbox_controller.SetButtonState(Xbox360Button.Down, new_state.dpad_down);
			xbox_controller.SetButtonState(Xbox360Button.Left, new_state.dpad_left);

			xbox_controller.SetButtonState(Xbox360Button.LeftShoulder, new_state.shoulder_left);
			xbox_controller.SetButtonState(Xbox360Button.RightShoulder, new_state.shoulder_right);

			xbox_controller.SetAxisValue(Xbox360Axis.LeftThumbX, new_state.axis_left_x);
			xbox_controller.SetAxisValue(Xbox360Axis.LeftThumbY, new_state.axis_left_y);
			xbox_controller.SetAxisValue(Xbox360Axis.RightThumbX, new_state.axis_right_x);
			xbox_controller.SetAxisValue(Xbox360Axis.RightThumbY, new_state.axis_right_y);

			xbox_controller.SetSliderValue(Xbox360Slider.LeftTrigger, new_state.trigger_left);
			xbox_controller.SetSliderValue(Xbox360Slider.RightTrigger, new_state.trigger_right);

			xbox_controller.SubmitReport();

			current_state = new_state;
		}
	}
}
