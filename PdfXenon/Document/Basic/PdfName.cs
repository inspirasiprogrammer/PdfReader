﻿using System;
using System.Text;

namespace PdfXenon.Standard
{
    public class PdfName : PdfObject
    {
        public PdfName(PdfObject parent, ParseName name)
            : base(parent, name)
        {
        }

        public override string ToString()
        {
            return $"PdfName {Value}";
        }

        public override int ToDebug(StringBuilder sb, int indent)
        {
            string output = $"/{Value}";
            sb.Append(output);
            return indent + output.Length;
        }

        public ParseName ParseName { get => ParseObject as ParseName; }
        public string Value { get => ParseName.Value; }
    }
}