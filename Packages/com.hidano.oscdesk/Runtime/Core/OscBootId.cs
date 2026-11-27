// OscBootId.cs — 起動ごとの ID(DESIGN.md D-164・HID-490)。UnityEngine に依存しない。
using System;

namespace OscDesk.Receiver
{
    /// <summary>
    /// 受け口の起動ごとの ID。OSCQuery の HOST_INFO の独自の拡張 <see cref="HostInfoKey"/> と、<c>/sys/pong</c> の第 2 引数
    /// (<c>int seq, string bootId</c>)に載せる。ブリッジは ping のたびに比べ、変わったら「ターゲットが起動した」とみなす。
    /// 時刻は使わない(時計の巻き戻し・同じ時刻の再起動で同じ値になり得るため)。Unity では Play の開始ごとに作り直す。
    /// </summary>
    public static class OscBootId
    {
        /// <summary>HOST_INFO に載せるキー(値は文字列)。他の Issue と共有の取り決めなので変えない。</summary>
        public const string HostInfoKey = "X_OSCDESK_BOOT_ID";

        private static string current;
        private static readonly object Gate = new object();

        /// <summary>今の起動の ID(32 桁の小文字 16 進。初めて使うときに作る)。</summary>
        public static string Current
        {
            get
            {
                lock (Gate)
                {
                    return current ?? (current = Guid.NewGuid().ToString("N"));
                }
            }
        }

        /// <summary>新しい ID に取り替える(Play の開始など、受け口が「起動し直した」とみなすとき)。</summary>
        public static string Renew()
        {
            lock (Gate)
            {
                current = Guid.NewGuid().ToString("N");
                return current;
            }
        }
    }
}
