using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // PadId assignment/compaction and virtual controller (ViGEmBus) creation/destruction, split
    // out of Program.cs (JoyconManager) into its own file per DOCS/CONTROLLERS-REFACTOR.md's
    // virtual-controller-lifecycle section - this is highest-stakes-surface #1 from "What must
    // not regress" (three prior regressions - fb3dca1/3d1c38a/156dcf3 - happened in exactly this
    // code), so it gets a dedicated, standalone home instead of living alongside the unrelated
    // device-scanning/enumeration code in Program.cs.
    //
    // Deliberately pairing-ignorant, by design, not by accident: this only ever needs to know
    // "does this controller currently want an active virtual controller, or is it passively
    // parked without one" - never *why* (Joy-Con is the only device type known to pair two
    // physical units into one logical controller; that quirk, and the "which half is the loser"
    // decision, stays in the auto-join/JoinOrSplitJoycon code that calls into these primitives,
    // not here). Still a partial class of JoyconManager, not a separate class - these methods
    // read/write j and form directly, same as everything else in Program.cs, just physically
    // relocated.
    //
    // Single-instance parameters (NextAvailablePadId's exclude, AssignPadId/CreateOutputControllers
    // /DestroyOutputControllers/ReassignSplitOffJoycon's jc) were Controller-typed as of step 4
    // Phase D already. The loop variables that iterate j directly (CleanUp/ReassignPadIds/
    // ResolveStalePadIdCollisions/DumpState/NextAvailablePadId's own loop) are Controller-typed
    // too as of step 4 Phase H+I, now that j itself is ConcurrentList<Controller>.
    public partial class JoyconManager {
        // Smallest PadId not currently in use by a connected controller - see the call site for
        // why j.Count itself isn't safe to use directly. exclude lets a caller ask "what's free
        // for this specific controller" without that controller's own (about-to-be-replaced)
        // PadId counting against itself - see ReassignSplitOffJoycon, whose caller unlinks
        // .other before calling in, which would otherwise flip it from "passive, ignored" to
        // "solo, counted as using its own stale value" for this computation alone.
        int NextAvailablePadId(Controller exclude = null) {
            var used = new HashSet<int>();
            foreach (Controller v in j) {
                if (v == exclude)
                    continue;

                // A joined pair's passive half doesn't occupy a visible LED/player slot on its
                // own - its physical LED just mirrors its active partner's (see ReassignPadIds).
                // Its PadId field is deliberately left untouched while paired, parked until it
                // splits back off (see ReassignSplitOffJoycon), so it must not count as "in use"
                // here - otherwise a genuinely new controller gets skipped past a slot nothing
                // visible is actually occupying.
                bool isPassiveHalf = v.other != null && v.other != v && v.out_xbox == null && v.out_ds4 == null;
                if (isPassiveHalf)
                    continue;
                used.Add(v.PadId);
            }

            int id = 0;
            while (used.Contains(id))
                id++;
            return id;
        }

        void CleanUp() { // removes dropped controllers from list
            List<Controller> rem = new List<Controller>();
            List<Controller> droppedNotify = new List<Controller>();
            List<JoyconController> partnerNotify = new List<JoyconController>();

            foreach (Controller joycon in j) {
                if (joycon.state == Controller.state_.DROPPED) {
                    // Capture the pair partner (if any) before Detach/nulling below, so
                    // HandleJoyconDropped can still find whichever slot(s) need fixing up -
                    // the dropped Joycon's own slot, and/or the surviving partner's.
                    JoyconController partner = (joycon.other != null && joycon.other != joycon) ? joycon.other : null;

                    if (joycon.other != null) {
                        JoyconController survivor = joycon.other;
                        survivor.other = null; // The other of the other is the joycon itself

                        // The survivor needs its own controller back if the dropped half was the
                        // one actively driving the pair's shared controller (see JoinOrSplitJoycon/
                        // the auto-join block above, which disconnects whichever side loses the
                        // join) - otherwise it's left solo with no virtual controller at all.
                        // No-op if the survivor already has one (it was the active side).
                        CreateOutputControllers(survivor);
                    }

                    DebugLog.Write("CleanUp: dropping controller pad=" + joycon.PadId +
                        " path=" + joycon.path);
                    // Teardown must never decide whether the pad leaves the list. Everything
                    // below talks to hardware that may already be gone, and CheckForNewControllers
                    // Time has no handler of its own - so an exception escaping here aborted the
                    // entire scan pass, leaving the pad in j still holding its virtual target and
                    // killing enumeration outright. Every later pass then died in the same place,
                    // which is why the ghost never reconciled short of restarting the service.
                    try {
                        joycon.Detach(true);
                        // A firmware-initiated power off (PS held past the controller's own
                        // hardware timeout) executes none of our power-off code - the controller
                        // just goes dark while still cabled, so nothing ever armed the wake monitor
                        // and a later PS press has no listener. This is the one place every dropped
                        // controller passes through, so recover from here. No-ops when we powered
                        // it off ourselves, mid pairing, on a non-Bluetooth-preferred profile, or
                        // when the device is really gone (its own open fails). Detach above already
                        // closed the pad's handle, so this opens its own.
                        (joycon as DualSenseController)?.RecoverFromFirmwarePowerOff();
                        // UsbAudioLoopback lives outside the controller classes entirely (driven
                        // straight from Program.cs/form, not a per-controller Detach hook like
                        // Bluetooth audio's OnDetachingWhileAttached), so nothing else stops it
                        // when a controller drops - this is the one place every dropped controller
                        // actually passes through. Always safe to call: Stop is idempotent and a
                        // no-op if nothing was ever started for this pad.
                        form.StopUsbAudioLoopback(joycon.PadId);
                    } catch (Exception ex) {
                        DebugLog.Write("CleanUp: teardown failed for pad=" + joycon.PadId +
                            " - removing anyway: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    rem.Add(joycon);

                    droppedNotify.Add(joycon);
                    partnerNotify.Add(partner);

                    form.AppendTextBox("Removed dropped controller. Can be reconnected.\r\n");
                }
            }

            foreach (Controller v in rem)
                j.Remove(v);

            // Compact PadIds for whatever's left so dropping down to fewer controllers -
            // especially down to just one - reads as player 1 again, matching what unplugging
            // and replugging the physical controller already does today (which is exactly what
            // this is standing in for, so the user doesn't have to do that by hand). See
            // ReassignPadIds for why this also means tearing down and recreating each affected
            // survivor's virtual controller, not just its LED.
            if (rem.Count > 0)
                ReassignPadIds();

            // Notified only after removal is fully done (list + pairing), not before - MainForm's
            // implementation doesn't care (it acts on the passed-in Joycon references directly,
            // not by re-reading the controller list), but HeadlessJoyconHost's does: it rebuilds
            // its status snapshot from the live list, and building that snapshot before the
            // drop was actually applied meant a GUI connected to a running service never saw a
            // disconnect at all until it reconnected (the next change - a different controller
            // connecting - would finally push a snapshot that happened to already be missing it).
            for (int i = 0; i < droppedNotify.Count; i++)
                form.HandleJoyconDropped(droppedNotify[i], partnerNotify[i]);
        }

        // Compacts PadId assignments for whatever's currently in j down to 0..n-1, based on each
        // controller's existing PadId order. Dropping controllers can leave survivors holding
        // non-contiguous PadIds (e.g. players 1 and 3 remain after player 2 disconnects) - this
        // closes the gaps, most visibly in the "down to just one controller left" case, which
        // should read as player 1 without the user having to unplug/replug it by hand.
        //
        // Unlike the old LED-only version, a controller whose PadId actually changes gets its
        // virtual controller torn down and recreated at the new identity via AssignPadId, not
        // just a re-painted LED: ViGEmBus has no API to rename an already-Connect()ed target's
        // XInput slot, so a fresh Connect() is how identity changes here - the same "destroy and
        // recreate" pattern already tested and confirmed working for join/split (see the auto-
        // join loser handling above and JoinOrSplitJoycon). A controller whose PadId doesn't
        // change is left completely untouched - no churn, no risk to a game already using it.
        //
        // A joined pair shares one rank between both halves for LED display, matching
        // Joycon.other's setter (which also does Math.Min(...) between a pair to pick one LED
        // value for both) - but only the pair's active half (the one actually holding a virtual
        // controller) goes through AssignPadId; the passive half is deliberately left without a
        // virtual controller (see the auto-join/JoinOrSplitJoycon loser handling) and must stay
        // that way, or a joined pair goes back to showing up as two separate XInputs.
        //
        // Critically, the passive half's actual PadId field is left completely untouched here -
        // only its LED is refreshed. It already has its own distinct PadId from whenever it was
        // originally connected (join never touches PadId, only out_xbox/out_ds4), parked and
        // waiting for whenever this pair splits back apart. Overwriting it to match the active
        // half's compacted rank - as an earlier version of this method did - made both halves
        // share the same PadId, so splitting the pair later produced two controllers that both
        // claimed to be the same player instead of two distinct ones.
        void ReassignPadIds() {
            var ranked = new List<Controller>(j);
            ranked.Sort((a, b) => a.PadId.CompareTo(b.PadId));

            var assigned = new HashSet<Controller>();
            int rank = 0;
            foreach (Controller jc in ranked) {
                if (assigned.Contains(jc))
                    continue;

                assigned.Add(jc);
                bool isPair = jc.other != null && jc.other != jc;
                if (isPair)
                    assigned.Add(jc.other);

                Controller active = jc;
                Controller passive = null;
                if (isPair) {
                    bool jcHasOutput = jc.out_xbox != null || jc.out_ds4 != null;
                    active = jcHasOutput ? jc : jc.other;
                    passive = active == jc ? jc.other : jc;
                }

                AssignPadId(active, rank);
                if (passive != null)
                    passive.RequestLEDUpdate(rank);

                rank++;
            }
        }

        // Full dump of every controller's PadId/pairing/output state - see DebugLog (off by
        // default, gated behind the DebugLogging AppSetting). Called at every PadId-affecting
        // decision point (connect, join, split, rank compaction) so player-slot/LED bugs can be
        // diagnosed from debug.log instead of guessed at.
        void DumpState(string tag) {
            var sb = new StringBuilder(tag).Append(':');
            foreach (Controller v in j) {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " [pad={0} other={1} hasXbox={2} hasDs4={3}]",
                    v.PadId,
                    v.other == null ? "null" : (v.other == v ? "self" : v.other.PadId.ToString(CultureInfo.InvariantCulture)),
                    v.out_xbox != null, v.out_ds4 != null);
            }
            DebugLog.Write(sb.ToString());
        }

        void AssignPadId(Controller jc, int newPadId) {
            if (jc.PadId == newPadId)
                return;

            DebugLog.Write(string.Format(CultureInfo.InvariantCulture, "AssignPadId: pad {0} -> {1}", jc.PadId, newPadId));
            jc.PadId = newPadId;
            ResolveStalePadIdCollisions();
            jc.RequestLEDUpdate(newPadId);

            DestroyOutputControllers(jc);
            CreateOutputControllers(jc);
        }

        // Called right after a Joycon splits off from a pair (see JoinOrSplitJoycon's split
        // branch in MainForm.cs/HeadlessJoyconHost.cs) - its PadId was deliberately left
        // untouched while it was the passive half (see ReassignPadIds's comment on why), and
        // NextAvailablePadId stopped counting it as "in use" the moment it went passive, so a
        // different controller may already have claimed that same number. Give it a fresh,
        // guaranteed-free identity instead of assuming the old one is still safe to reuse. A
        // no-op (via AssignPadId's own check) if nothing actually claimed it in the meantime.
        public void ReassignSplitOffJoycon(Controller jc) {
            AssignPadId(jc, NextAvailablePadId(jc));
        }

        // NextAvailablePadId deliberately skips a joined pair's passive half when computing
        // what's free (it has no virtual controller and doesn't occupy a visible LED slot), so
        // whenever something claims a "next available" number, that number may already be held
        // by a passive half that's still parked on it. Both then share one PadId, which breaks
        // anything that assumes PadId uniquely identifies a controller: BuildSnapshot/
        // RenderSnapshot's pair de-duplication (a colliding record gets mistaken for the pair's
        // already-rendered passive half and silently dropped from the UI - the colliding
        // controller works fine, it just never appears) and remote-mode command routing by PadId.
        // Called after every real PadId change (see AssignPadId) - loops because resolving one
        // collision can, in principle, land on a different pair's stale value in turn.
        //
        // Deliberately bookkeeping-only: unlike AssignPadId, this must NOT call RequestLEDUpdate
        // or touch out_xbox/out_ds4 - the passive half being moved has no virtual controller to
        // recreate, and its physical LED is intentionally left showing its active partner's
        // shared rank (see ReassignPadIds), not its own PadId. Only the in-memory identity moves;
        // nothing observable to the user changes. Two passive halves sharing a stale number is
        // left alone - neither is ever treated as "in use", so it can't cause this symptom.
        void ResolveStalePadIdCollisions() {
            bool changed = true;
            while (changed) {
                changed = false;
                foreach (Controller v in j) {
                    bool isPassiveHalf = v.other != null && v.other != v && v.out_xbox == null && v.out_ds4 == null;
                    if (!isPassiveHalf)
                        continue;

                    foreach (Controller other in j) {
                        if (other == v || other.PadId != v.PadId)
                            continue;
                        bool otherIsPassiveHalf = other.other != null && other.other != other && other.out_xbox == null && other.out_ds4 == null;
                        if (otherIsPassiveHalf)
                            continue;

                        int freed = v.PadId;
                        v.PadId = NextAvailablePadId();
                        DebugLog.Write(string.Format(CultureInfo.InvariantCulture,
                            "ResolveStalePadIdCollisions: passive half moved pad {0} -> {1} (collided with pad={2} hasXbox={3} hasDs4={4})",
                            freed, v.PadId, other.PadId, other.out_xbox != null, other.out_ds4 != null));
                        changed = true;
                        break;
                    }
                    if (changed)
                        break;
                }
            }
        }

        // Unconditionally tears down whatever virtual output(s) jc currently has, if any - the
        // primitive every "this controller shouldn't have a virtual controller right now" call
        // site should share instead of hand-rolling its own out_xbox/out_ds4 Disconnect(), per
        // DOCS/CONTROLLERS-REFACTOR.md's virtual-controller-lifecycle section: the auto-join
        // block's loser-destroy logic used to duplicate this inline rather than reusing
        // CreateOutputControllers/AssignPadId's existing pattern, which is exactly the kind of
        // duplication that makes a clean lifecycle-module extraction impossible. Deliberately
        // pairing-ignorant - it destroys whatever it's given, it doesn't decide who's a "loser".
        public void DestroyOutputControllers(Controller jc) {
            if (jc.out_xbox != null) {
                try { jc.out_xbox.Disconnect(); } catch { }
                jc.out_xbox = null;
            }
            if (jc.out_ds4 != null) {
                try { jc.out_ds4.Disconnect(); } catch { }
                jc.out_ds4 = null;
            }
            if (jc.out_dualsense != null) {
                try { jc.out_dualsense.Disconnect(); } catch { }
                jc.out_dualsense = null;
            }
        }

        // Shared by attach, profile changes, AssignPadId, and survivor restoration. Reconciles
        // both directions: changing a profile from Xbox to DS4/Disabled removes the old target,
        // while enabling an output creates and connects the requested target.
        // The bus driver is not ready the instant the machine resumes: the first target Connect()
        // after a wake throws a Win32Exception carrying a stale last-error (seen as
        // ERROR_ENVVAR_NOT_FOUND, ERROR_IO_PENDING and ERROR_SUCCESS - none of which describe
        // anything real), while the very next Connect on that same client succeeds about a second
        // later. Losing the first one is not cosmetic: the pad is dropped and re-adopted a few
        // seconds afterwards, and for a controller on Bluetooth with a cable attached that
        // re-adoption lands before the Bluetooth pad has reached IMU_DATA_OK - so
        // HasLiveBluetoothDualSense reads false, the transport decision is made on a false premise,
        // and the pad is put through the wrong power-off path. Retry briefly rather than let one
        // unready driver call decide any of that.
        static void ConnectVirtualTarget(Action connect) {
            const int attempts = 4;
            for (int attempt = 1; ; attempt++) {
                try {
                    connect();
                    return;
                } catch (Exception e) when (attempt < attempts) {
                    DebugLog.Write("Virtual target Connect failed (attempt " + attempt + " of " +
                        attempts + "), retrying: " + e.GetType().Name + ": " + e.Message);
                    Thread.Sleep(250);
                }
            }
        }

        void CreateOutputControllers(Controller jc) {
            string useAs = ControllerMappings.OptionValue(
                ControllerMappings.ProfileIdFor(jc), "UseAs");
            bool useXbox = useAs == ControllerMappings.UseAsXbox360;
            bool useXboxViiper = useAs == ControllerMappings.UseAsXbox360Viiper;
            bool useDs4 = useAs == ControllerMappings.UseAsDualShock4;
            bool useDualSenseViiper = useAs == ControllerMappings.UseAsDualSenseViiper;
            bool usePassthrough = useAs == ControllerMappings.UseAsPassthrough;

            // Passthrough is the one "UseAs" value that also reaches outside virtual-output
            // creation - see ControllerMappings.UseAsPassthrough's comment. Every other value
            // (including UseAsNone/Disabled) keeps the physical device HidHide-blocked, matching
            // TryHideController's default at connect time.
            ReconcileHidHideForProfile(jc, !usePassthrough);

            // A profile switching between the two Xbox 360 backends (or off) always tears down
            // whatever's currently there first - out_xbox is one field shared by both backends
            // (see IOutputControllerXbox360), so this check alone already covers both without
            // needing to know which one is actually live.
            if (!useXbox && !useXboxViiper && jc.out_xbox != null) {
                try { jc.out_xbox.Disconnect(); } catch { }
                jc.out_xbox = null;
            }
            if (!useDs4 && jc.out_ds4 != null) {
                try { jc.out_ds4.Disconnect(); } catch { }
                jc.out_ds4 = null;
            }
            if (!useDualSenseViiper && jc.out_dualsense != null) {
                try { jc.out_dualsense.Disconnect(); } catch { }
                jc.out_dualsense = null;
            }

            if ((useXbox || useDs4) && !Program.EnsureVigemClient())
                return;
            if ((useXboxViiper || useDualSenseViiper) && !Program.EnsureViiperServer())
                return;

            if (useXbox && jc.out_xbox == null) {
                jc.out_xbox = new VirtualOutput.OutputControllerXbox360();
                jc.out_xbox.FeedbackReceived += jc.ReceiveRumble;
                ConnectVirtualTarget(jc.out_xbox.Connect);
            }
            if (useXboxViiper && jc.out_xbox == null) {
                jc.out_xbox = new VirtualOutput.OutputControllerXbox360Viiper();
                jc.out_xbox.FeedbackReceived += jc.ReceiveRumble;
                ConnectVirtualTarget(jc.out_xbox.Connect);
            }
            if (useDs4 && jc.out_ds4 == null) {
                jc.out_ds4 = new VirtualOutput.OutputControllerDualShock4();
                jc.out_ds4.FeedbackReceived += jc.Ds4_FeedbackReceived;
                ConnectVirtualTarget(jc.out_ds4.Connect);
            }
            if (useDualSenseViiper && jc.out_dualsense == null) {
                jc.out_dualsense = new VirtualOutput.OutputControllerDualSenseViiper();
                jc.out_dualsense.FeedbackReceived += jc.Ds4_FeedbackReceived;
                ConnectVirtualTarget(jc.out_dualsense.Connect);
            }

            if (jc is XboxController xbox && !xbox.ReconcileNativeInputSlot())
                throw new InvalidOperationException(
                    "The physical Xbox controller lost its native XInput slot.");

            if (!jc.RumbleEnabled) {
                jc.StopRumble();
                if (jc.other != null && jc.other != jc)
                    jc.other.StopRumble();
            }
        }
    }
}
