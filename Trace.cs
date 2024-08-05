using System;

namespace MetadataParser
{
    static class Trace
    {
        static internal void Write(string s, params object[] o)
        {
            System.Diagnostics.Trace.Write(String.Format(s, o));
        }

        static internal void WriteLine()
        {
            System.Diagnostics.Trace.WriteLine("");
        }

        static internal void WriteLine(string s, params object[] o)
        {
            System.Diagnostics.Trace.WriteLine(String.Format(s, o));
        }
    }
}
