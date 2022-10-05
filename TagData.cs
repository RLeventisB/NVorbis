using System;
using System.Collections.Generic;
using System.Linq;

namespace NVorbis
{
    public class TagData
    {
        static readonly IReadOnlyList<string> s_emptyList = new List<string>();
        readonly Dictionary<string, IList<string>> _tags;
        public TagData(string vendor, string[] comments)
        {
            EncoderVendor = vendor;

            var tags = new Dictionary<string, IList<string>>();
            for (var i = 0; i < comments.Length; i++)
            {
                var parts = comments[i].Split('=');
                if (parts.Length == 1)
                {
                    parts = new[] { parts[0], string.Empty };
                }

                var bktIdx = parts[0].IndexOf('[');
                if (bktIdx > -1)
                {
                    parts[1] = parts[0].Substring(bktIdx + 1, parts[0].Length - bktIdx - 2)
                                       .ToUpper(System.Globalization.CultureInfo.CurrentCulture)
                                     + ": "
                                     + parts[1];
                    parts[0] = parts[0][..bktIdx];
                }

                if (tags.TryGetValue(parts[0].ToUpperInvariant(), out var list))
                {
                    list.Add(parts[1]);
                }
                else
                {
                    tags.Add(parts[0].ToUpperInvariant(), new List<string> { parts[1] });
                }
            }
            _tags = tags;
        }
        public string GetTagSingle(string key, bool concatenate = false)
        {
            var values = GetTagMulti(key);
            if (values.Count > 0)
            {
                if (concatenate)
                {
                    return string.Join(Environment.NewLine, values.ToArray());
                }
                return values[values.Count - 1];
            }
            return string.Empty;
        }
        public IReadOnlyList<string> GetTagMulti(string key)
        {
            if (_tags.TryGetValue(key.ToUpperInvariant(), out var values))
            {
                return (IReadOnlyList<string>)values;
            }
            return s_emptyList;
        }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> All => (IReadOnlyDictionary<string, IReadOnlyList<string>>)_tags;
        public string EncoderVendor { get; }
    }
}