// OscSyncTracker.cs — 同期(自動の再送・手動の同期)の一連の受信を見分ける(DESIGN.md D-165・D-166・HID-490)。
// UnityEngine に依存しない。
using System;
using System.Collections.Generic;

namespace OscDesk.Receiver
{
    /// <summary>同期 1 回分の記録。<see cref="OscSyncTracker"/> の Began / Ended で渡す。</summary>
    public sealed class OscSyncSession
    {
        /// <summary>始まりの合図の第 1 引数の文字列(例 "auto" / "manual")。無ければ空。</summary>
        public string Reason { get; }

        /// <summary>始まりの合図を受けた時刻(Tracker に渡した時計の秒)。</summary>
        public double StartedAt { get; }

        /// <summary>始まりから終わりまでに受けた、/sys/* 以外のメッセージの数(保持値の一連の受信)。</summary>
        public int Messages { get; internal set; }

        /// <summary>終わりの合図で閉じたら true。時間切れ・次の始まりの合図・受け口の停止で閉じたら false。</summary>
        public bool Completed { get; internal set; }

        /// <summary>閉じた時刻。閉じるまでは負。</summary>
        public double EndedAt { get; internal set; } = -1;

        public OscSyncSession(string reason, double startedAt)
        {
            Reason = reason ?? string.Empty;
            StartedAt = startedAt;
        }
    }

    /// <summary>
    /// 同期の合図を受けて、保持値の一連の受信の始まりと終わりを知らせる。受信側(フォークのアプリ)が生成物を作り直すなどの
    /// 処理を書く場所で、作り直しそのものは受け口は行わない(D-166)。
    /// 合図のアドレス(<see cref="BeginAddress"/> / <see cref="EndAddress"/>)は仮置きで、oscdesk 側の HID-487 で確定する。
    /// 引数は省いてよく、始まりの第 1 引数が文字列なら <see cref="OscSyncSession.Reason"/> に入れる。
    /// </summary>
    public sealed class OscSyncTracker
    {
        /// <summary>同期の始まりの合図(仮置き。HID-487 で確定)。</summary>
        public const string BeginAddress = "/sys/sync/begin";

        /// <summary>同期の終わりの合図(仮置き。HID-487 で確定)。</summary>
        public const string EndAddress = "/sys/sync/end";

        /// <summary>終わりの合図が届かないときに閉じるまでの秒数の既定(UDP なので終わりが落ちることがある)。</summary>
        public const double DefaultTimeoutSeconds = 10;

        private OscSyncSession active;
        private double lastActivityAt;

        /// <summary>始まり(または最後の受信)からこの秒数を過ぎても終わりが来なければ、未完了として閉じる。0 以下で閉じない。</summary>
        public double TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

        /// <summary>同期の最中か。</summary>
        public bool InSync => active != null;

        /// <summary>今の同期(最中でなければ null)。</summary>
        public OscSyncSession Active => active;

        /// <summary>同期が始まった。</summary>
        public event Action<OscSyncSession> Began;

        /// <summary>同期が閉じた(Completed で終わりの合図によるかが分かる)。</summary>
        public event Action<OscSyncSession> Ended;

        /// <summary>
        /// 合図なら処理して true(始まり・終わりの event を呼ぶ)。合図でなければ何もせず false。
        /// 同期の最中に次の始まりが来たら、前のものを未完了で閉じてから始める。始まりの無い終わりは無視する(true は返す)。
        /// </summary>
        public bool TryHandle(string address, IReadOnlyList<object> args, double now)
        {
            if (string.Equals(address, BeginAddress, StringComparison.Ordinal))
            {
                if (active != null)
                {
                    Close(false, now);
                }

                var reason = args != null && args.Count > 0 && args[0] is string text ? text : string.Empty;
                active = new OscSyncSession(reason, now);
                lastActivityAt = now;
                Began?.Invoke(active);
                return true;
            }

            if (string.Equals(address, EndAddress, StringComparison.Ordinal))
            {
                if (active != null)
                {
                    Close(true, now);
                }

                return true;
            }

            return false;
        }

        /// <summary>/sys/* 以外のメッセージを 1 件受けた(同期の最中なら数える)。</summary>
        public void NoteMessage(double now)
        {
            if (active == null)
            {
                return;
            }

            active.Messages += 1;
            lastActivityAt = now;
        }

        /// <summary>時間切れを確かめる(フレームごとに呼ぶ)。閉じたら true。</summary>
        public bool Poll(double now)
        {
            if (active == null || TimeoutSeconds <= 0 || now - lastActivityAt < TimeoutSeconds)
            {
                return false;
            }

            Close(false, now);
            return true;
        }

        /// <summary>最中の同期を未完了で閉じる(受け口の停止で)。</summary>
        public void Abort(double now)
        {
            if (active != null)
            {
                Close(false, now);
            }
        }

        private void Close(bool completed, double now)
        {
            var session = active;
            active = null;
            session.Completed = completed;
            session.EndedAt = now;
            Ended?.Invoke(session);
        }
    }
}
