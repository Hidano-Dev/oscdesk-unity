// OscDefinitionRows.cs — 定義 JSON から対応表に必要な行を求める(インスペクタの警告と行の作成に使う)。UnityEngine に依存しない。
// 一覧の送信の検査(OscStringList)と同じファイルにあったものを、一覧の送信の撤去(HID-490)で分けた。
using System;
using System.Collections.Generic;

namespace OscDesk.Receiver
{
    /// <summary>定義から対応表に必要な行(アドレスと型の提案)を取り出す。</summary>
    public static class OscDefinitionRows
    {
        /// <summary>
        /// パラメータのアドレスに加え、optionsFrom.request は Trigger の行として求める(アプリが一覧を返す関数を登録する口)。
        /// optionsFrom.address は Unity が送る側なので行の不足として扱わない。
        /// 同じアドレスは先に現れたものを採る。
        /// </summary>
        public static List<KeyValuePair<string, OscBindingKind>> Expected(IEnumerable<OscDefinitionParameter> parameters)
        {
            var result = new List<KeyValuePair<string, OscBindingKind>>();
            foreach (var binding in ExpectedBindings(parameters, null))
            {
                result.Add(new KeyValuePair<string, OscBindingKind>(binding.Address, binding.Kind));
            }

            return result;
        }

        /// <summary>
        /// パラメータとレコードから求める行。並びはパラメータ → レコード(Record の行、フィールド id と Record Row Ids つき)
        /// → OscDesk から受けるキーの一覧(keysFrom.source "ui" の keysFrom.address、Strings。D-075)→ 要求のアドレス
        /// (パラメータの optionsFrom.request → レコードの keysFrom.request → レコードのフィールドの optionsFrom.request、いずれも Trigger)。
        /// それ以外の keysFrom.address と optionsFrom.address は Unity が送る側なので行の不足として扱わない(D-057 と同じ)。
        /// 同じアドレスは先に現れたものを採る。
        /// </summary>
        public static List<OscExpectedBinding> ExpectedBindings(IEnumerable<OscDefinitionParameter> parameters, IEnumerable<OscDefinitionRecord> records)
        {
            var parameterList = new List<OscDefinitionParameter>(parameters ?? Array.Empty<OscDefinitionParameter>());
            var recordList = new List<OscDefinitionRecord>(records ?? Array.Empty<OscDefinitionRecord>());
            var result = new List<OscExpectedBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in parameterList)
            {
                if (seen.Add(parameter.Address))
                {
                    result.Add(new OscExpectedBinding(parameter.Address, parameter.SuggestKind()));
                }
            }

            foreach (var record in recordList)
            {
                if (seen.Add(record.Address))
                {
                    result.Add(new OscExpectedBinding(record.Address, OscBindingKind.Record, record.FieldIds(), record.UsesRowIds));
                }
            }

            foreach (var record in recordList)
            {
                if (record.ReceivesKeys && record.KeysFromAddress.Length > 0 && seen.Add(record.KeysFromAddress))
                {
                    result.Add(new OscExpectedBinding(record.KeysFromAddress, OscBindingKind.Strings));
                }
            }

            void AddRequest(string request)
            {
                if (request.Length > 0 && seen.Add(request))
                {
                    result.Add(new OscExpectedBinding(request, OscBindingKind.Trigger));
                }
            }

            foreach (var parameter in parameterList)
            {
                AddRequest(parameter.OptionsFromRequest);
            }

            foreach (var record in recordList)
            {
                AddRequest(record.KeysFromRequest);
            }

            foreach (var record in recordList)
            {
                foreach (var field in record.Fields)
                {
                    AddRequest(field.OptionsFromRequest);
                }
            }

            return result;
        }

        /// <summary>
        /// 求める行のうち Record の行をアドレスで引けるようにする(同じアドレスは先に現れたものを採る。AuditWithDefinition と同じ)。
        /// インスペクタの「Record の行のフィールド id を定義に合わせる」が、既存の行の Record Fields と Record Row Ids をここから揃える。
        /// </summary>
        public static Dictionary<string, OscExpectedBinding> RecordBindingsByAddress(IEnumerable<OscExpectedBinding> expected)
        {
            var result = new Dictionary<string, OscExpectedBinding>(StringComparer.Ordinal);
            foreach (var binding in expected ?? Array.Empty<OscExpectedBinding>())
            {
                if (binding.Kind == OscBindingKind.Record && !result.ContainsKey(binding.Address))
                {
                    result.Add(binding.Address, binding);
                }
            }

            return result;
        }
    }
}
