using System.Collections.Generic;
using NUnit.Framework;

namespace OscDesk.Receiver.Tests
{
    // 同期の合図(仮置きの /sys/sync/begin・end。HID-487 で確定)から始まりと終わりを知らせる(HID-490・D-166)
    public sealed class OscSyncTrackerTests
    {
        [Test]
        public void Begin_messages_end_make_one_completed_session()
        {
            var tracker = new OscSyncTracker();
            var began = new List<OscSyncSession>();
            var ended = new List<OscSyncSession>();
            tracker.Began += began.Add;
            tracker.Ended += ended.Add;

            Assert.That(tracker.TryHandle("/mix/level", new object[] { 1f }, 0), Is.False);
            tracker.NoteMessage(0); // 同期の外は数えない
            Assert.That(tracker.TryHandle(OscSyncTracker.BeginAddress, new object[] { "auto" }, 1), Is.True);
            Assert.That(tracker.InSync, Is.True);
            tracker.NoteMessage(1.1);
            tracker.NoteMessage(1.2);
            Assert.That(tracker.TryHandle(OscSyncTracker.EndAddress, null, 1.5), Is.True);

            Assert.That(began.Count, Is.EqualTo(1));
            Assert.That(ended.Count, Is.EqualTo(1));
            Assert.That(ended[0], Is.SameAs(began[0]));
            Assert.That(ended[0].Reason, Is.EqualTo("auto"));
            Assert.That(ended[0].Messages, Is.EqualTo(2));
            Assert.That(ended[0].Completed, Is.True);
            Assert.That(ended[0].EndedAt, Is.EqualTo(1.5));
            Assert.That(tracker.InSync, Is.False);
        }

        [Test]
        public void End_without_begin_is_ignored_and_begin_without_args_has_empty_reason()
        {
            var tracker = new OscSyncTracker();
            var ended = 0;
            tracker.Ended += _ => ended += 1;
            Assert.That(tracker.TryHandle(OscSyncTracker.EndAddress, new object[0], 0), Is.True);
            Assert.That(ended, Is.EqualTo(0));

            tracker.TryHandle(OscSyncTracker.BeginAddress, new object[] { 3 }, 0);
            Assert.That(tracker.Active.Reason, Is.Empty);
        }

        [Test]
        public void Missing_end_times_out_and_a_new_begin_closes_the_previous_one()
        {
            var tracker = new OscSyncTracker { TimeoutSeconds = 2 };
            var ended = new List<OscSyncSession>();
            tracker.Ended += ended.Add;

            tracker.TryHandle(OscSyncTracker.BeginAddress, null, 0);
            tracker.NoteMessage(1.5);
            Assert.That(tracker.Poll(3), Is.False, "最後の受信から数える");
            Assert.That(tracker.Poll(3.6), Is.True);
            Assert.That(ended[0].Completed, Is.False);

            tracker.TryHandle(OscSyncTracker.BeginAddress, new object[] { "manual" }, 10);
            tracker.TryHandle(OscSyncTracker.BeginAddress, new object[] { "auto" }, 11);
            Assert.That(ended.Count, Is.EqualTo(2));
            Assert.That(ended[1].Reason, Is.EqualTo("manual"));
            Assert.That(ended[1].Completed, Is.False);
            Assert.That(tracker.Active.Reason, Is.EqualTo("auto"));

            tracker.Abort(12);
            Assert.That(ended.Count, Is.EqualTo(3));
            Assert.That(tracker.InSync, Is.False);
        }
    }
}
