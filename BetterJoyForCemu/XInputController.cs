using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpDX.XInput;

namespace BetterJoyForCemu {
    public class XInputController {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        public XInputController() {
            Console.WriteLine("Start XGamepadApp");
            // Initialize XInput
            var controllers = new[] { new SharpDX.XInput.Controller(UserIndex.One), new SharpDX.XInput.Controller(UserIndex.Two), new SharpDX.XInput.Controller(UserIndex.Three), new SharpDX.XInput.Controller(UserIndex.Four) };

            // Get 1st controller available
            SharpDX.XInput.Controller controller = null;
            foreach (var selectControler in controllers) {
                if (selectControler.IsConnected) {
                    controller = selectControler;
                    break;
                }
            }

            if (controller == null) {
                Console.WriteLine("No XInput controller installed");
            } else {

                Console.WriteLine("Found a XInput controller available");
                Console.WriteLine("Press buttons on the controller to display events");

                // Poll events from joystick
                var previousState = controller.GetState();
                while (controller.IsConnected) {
                    var state = controller.GetState();
                    if (previousState.PacketNumber != state.PacketNumber)
                        Console.WriteLine(state.Gamepad);
                    previousState = state;
                    Thread.Sleep(8);//8 miliseconds = 125Hz
                }
            }
            Console.WriteLine("End XGamepadApp");
        }

        /// <summary>
        /// Determines whether the specified key is pressed.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>
        ///   <c>true</c> if the specified key is pressed; otherwise, <c>false</c>.
        /// </returns>
    }
}
