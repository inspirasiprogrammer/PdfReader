﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfXenon.Standard
{
    public class PdfParser
    {
        public event EventHandler<PdfResolveEventArgs> ResolveReference;

        public PdfParser(Stream stream)
        {
            Tokenizer = new Tokenizer(stream);
        }

        public void ParseHeader(out int major, out int minor)
        {
            // The header is a comment token
            Tokenizer.IgnoreComments = false;
            TokenBase t = Tokenizer.GetToken();
            if (!(t is TokenComment))
                throw new ApplicationException("Missing PDF header.");

            TokenComment c = (TokenComment)t;
            if (!c.Comment.StartsWith("%PDF"))
                throw new ApplicationException("PDF Header must start with '%PDF'.");

            string[] splits = c.Comment.Substring(5).Split('.');
            if (splits.Length != 2)
                throw new ApplicationException("PDF Header must have a <major>.<minor> version number.");

            if (!int.TryParse(splits[0].Trim(), out major))
                throw new ApplicationException("Could not parse the header major version number.");

            if (!int.TryParse(splits[1].Trim(), out minor))
                throw new ApplicationException("Could not parse the header minor version number.");

            Tokenizer.IgnoreComments = true;
        }

        public long ParseXRefOffset()
        {
            return Tokenizer.GetXRefOffset();
        }

        public List<TokenXRefEntry> ParseXRef(long position)
        {
            Tokenizer.Position = position;
            return ParseXRef();
        }

        public List<TokenXRefEntry> ParseXRef()
        {
            Tokenizer.IgnoreComments = true;
            TokenBase t = Tokenizer.GetToken();
            ThrowIfNot<TokenKeyword>(t);

            TokenKeyword keyword = (TokenKeyword)t;
            if (keyword.Keyword != PdfKeyword.XRef)
                throw new ApplicationException($"Cross-reference table has keyword {keyword.Keyword.ToString()} instead of 'xref' at position {t.Position}.");

            List<TokenXRefEntry> entries = new List<TokenXRefEntry>();
            ParseXRefSections(entries);
            return entries;
        }

        public void ParseXRefSections(List<TokenXRefEntry> entries)
        {
            while (true)
            {
                TokenBase t = Tokenizer.GetToken();
                ThrowOnError(t);

                // Cross-reference table ends when we find a 'trailer' keyword instead of another section
                if ((t is TokenKeyword) && (((TokenKeyword)t).Keyword == PdfKeyword.Trailer))
                {
                    Tokenizer.PushToken(t);
                    return;
                }

                // Section starts with an integer object number
                TokenNumeric start = t as TokenNumeric;
                if ((start == null) || !start.IsInteger)
                    throw new ApplicationException($"Cross-reference section number must be an integer at position {t.Position}.");

                t = Tokenizer.GetToken();
                ThrowOnError(t);

                // Section then has an integer length number
                TokenNumeric length = t as TokenNumeric;
                if ((length == null) || !length.IsInteger)
                    throw new ApplicationException($"Cross-reference section length must be an integer at position {t.Position}.");

                // Load each line in the section
                for (int i = 0, id = start.Integer.Value; i < length.Integer; i++, id++)
                {
                    TokenBase entry = Tokenizer.GetXRefEntry(id);
                    ThrowOnError(entry);
                    entries.Add((TokenXRefEntry)entry);
                }
            }
        }

        public PdfDictionary ParseTrailer()
        {
            Tokenizer.IgnoreComments = true;
            TokenBase t = Tokenizer.GetToken();
            ThrowOnError(t);

            // Cross-reference table ends when we find a 'trailer' keyword instead of another section
            if (!(t is TokenKeyword) || (((TokenKeyword)t).Keyword != PdfKeyword.Trailer))
                throw new ApplicationException($"Trailer section must start with the 'trailer' keyword at position {t.Position}.");

            PdfObject obj = ParseObject();
            if ((obj == null) || !(obj is PdfDictionary))
                throw new ApplicationException($"Trailer section must contain a dictionary at position {t.Position}.");

            return (PdfDictionary)obj;
        }

        public PdfIndirectObject ParseIndirectObject(long position)
        {
            long restore = Tokenizer.Position;

            // Set correct position for parsing the randomly positioned object
            Tokenizer.Position = position;
            PdfIndirectObject ret = ParseIndirectObject();

            // Must restore original position so caller can continue from where they left off
            Tokenizer.Position = restore;

            return ret;
        }

        public PdfIndirectObject ParseIndirectObject()
        {
            Tokenizer.IgnoreComments = true;
            TokenBase t = Tokenizer.GetToken();
            ThrowOnEmptyOrError(t);

            // Indirect object starts with an identifier number
            if (!(t is TokenNumeric))
            {
                Tokenizer.PushToken(t);
                return null;
            }

            TokenNumeric id = (TokenNumeric)t;
            if (id.IsReal)
            {
                Tokenizer.PushToken(t);
                return null;
            }

            // Indirect object then has a generation number
            TokenBase u = Tokenizer.GetToken();
            ThrowOnEmptyOrError(u);

            if (!(u is TokenNumeric))
            {
                Tokenizer.PushToken(t);
                Tokenizer.PushToken(u);
                return null;
            }

            TokenNumeric gen = (TokenNumeric)u;
            if (gen.IsReal)
            {
                Tokenizer.PushToken(t);
                Tokenizer.PushToken(u);
                return null;
            }

            // Indirect object then has the keyword 'obj'
            TokenBase v = Tokenizer.GetToken();
            ThrowOnEmptyOrError(v);
            if (!(v is TokenKeyword) || ((v as TokenKeyword).Keyword != PdfKeyword.Obj))
            {
                Tokenizer.PushToken(t);
                Tokenizer.PushToken(u);
                Tokenizer.PushToken(v);
                return null;
            }

            // Get actual object that is the content
            PdfObject obj = ParseObject();
            if (obj == null)
                throw new ApplicationException($"Indirect object has missing content at position {t.Position}.");

            // Must be followed by either a 'endobj' or a 'stream'
            v = Tokenizer.GetToken();
            ThrowOnEmptyOrError(v);

            TokenKeyword keyword = (TokenKeyword)v;
            if (keyword == null)
                throw new ApplicationException($"Indirect object has missing 'endobj or 'stream' at position {v.Position}.");

            if (keyword.Keyword == PdfKeyword.EndObj)
                return new PdfIndirectObject(id, gen, obj);
            else if (keyword.Keyword == PdfKeyword.Stream)
            {
                PdfDictionary dictionary = obj as PdfDictionary;
                if (dictionary == null)
                    throw new ApplicationException($"Stream must be preceded by a dictionary at position {v.Position}.");

                if (!dictionary.ContainsKey("Length"))
                    throw new ApplicationException($"Stream dictionary must contain a 'Length' entry at position {v.Position}.");

                PdfDictEntry entry = dictionary["Length"];
                PdfObject lengthObj = entry.Object;

                // Resolve any object reference
                PdfObjectReference reference = lengthObj as PdfObjectReference;
                if (reference != null)
                    lengthObj = OnResolveReference(reference);

                PdfNumeric length = lengthObj as PdfNumeric;
                if ((length == null) || length.IsReal)
                    throw new ApplicationException($"Stream dictionary has a 'Length' entry that is not an integer entry at position {v.Position}.");

                if (length.Integer < 0)
                    throw new ApplicationException($"Stream dictionary has a 'Length' less than 0 at position {v.Position}.");

                byte[] bytes = Tokenizer.GetBytes(length.Integer);
                if (bytes == null)
                    throw new ApplicationException($"Cannot read in expected {length.Integer} bytes from stream at position {v.Position}.");

                // Stream contents must be followed by 'endstream'
                v = Tokenizer.GetToken();
                ThrowOnEmptyOrError(v);

                keyword = (TokenKeyword)v;
                if (keyword == null)
                    throw new ApplicationException($"Stream has missing 'endstream' after content at at position {v.Position}.");

                if (keyword.Keyword != PdfKeyword.EndStream)
                    throw new ApplicationException($"Stream has unexpected keyword {keyword.Keyword} instead of 'endstream' at position {v.Position}.");

                // Stream contents must be followed by 'endobj'
                v = Tokenizer.GetToken();
                ThrowOnEmptyOrError(v);

                keyword = (TokenKeyword)v;
                if (keyword == null)
                    throw new ApplicationException($"Indirect object has missing 'endobj' at position { v.Position }.");

                if (keyword.Keyword != PdfKeyword.EndObj)
                    throw new ApplicationException($"Indirect object has unexpected keyword {keyword.Keyword} instead of 'endobj' at position {v.Position}.");

                return new PdfIndirectObject(id, gen, new PdfStream(dictionary, bytes));
            }
            else
                throw new ApplicationException($"Indirect object has unexpected keyword {keyword.Keyword} at position {v.Position}.");
        }

        public PdfObject ParseObject()
        {
            Tokenizer.IgnoreComments = true;
            TokenBase t = Tokenizer.GetToken();
            ThrowOnEmptyOrError(t);

            if (t is TokenName)
                return new PdfName(t as TokenName);
            else if (t is TokenNumeric)
            {
                // An object reference starts with an integer number (the object identifier)
                TokenNumeric n = t as TokenNumeric;
                if (n.IsInteger)
                {
                    TokenBase t2 = Tokenizer.GetToken();
                    ThrowOnError(t2);

                    if (t2 is TokenNumeric)
                    {
                        // An object reference then has another integer number (the object generation)
                        TokenNumeric n2 = t2 as TokenNumeric;
                        if (n2.IsInteger)
                        {
                            TokenBase t3 = Tokenizer.GetToken();
                            ThrowOnError(t3);

                            if (t3 is TokenKeyword)
                            {
                                // An object reference finally has the R keyword
                                TokenKeyword k = t3 as TokenKeyword;
                                if (k.Keyword == PdfKeyword.R)
                                    return new PdfObjectReference(n, n2);
                            }

                            Tokenizer.PushToken(t3);
                        }
                    }

                    Tokenizer.PushToken(t2);
                }

                return new PdfNumeric(t as TokenNumeric);
            }
            else if (t is TokenHexString)
                return new PdfString(t as TokenHexString);
            else if (t is TokenLiteralString)
                return new PdfString(t as TokenLiteralString);
            else if (t is TokenArrayOpen)
            {
                List<PdfObject> objects = new List<PdfObject>();

                PdfObject entry = null;
                while (true)
                {
                    entry = ParseObject();
                    if (entry == null)
                        break;
                    else
                        ThrowOnEmptyOrError(t);

                    objects.Add(entry);
                }

                ThrowIfNot<TokenArrayClose>(Tokenizer.GetToken());
                return new PdfArray(t.Position, objects);
            }
            else if (t is TokenDictionaryOpen)
            {
                Dictionary<string, PdfDictEntry> entries = new Dictionary<string, PdfDictEntry>();

                PdfObject value1 = null;
                PdfObject value2 = null;
                while (true)
                {
                    value1 = ParseObject();
                    if (value1 == null)
                        break;
                    else
                        ThrowOnEmptyOrError(t);

                    // Key value must be a Name
                    PdfName name = value1 as PdfName;
                    if (name == null)
                        throw new ApplicationException($"Dictionary key must be a name instead of {name.GetType().Name} at position {name.Position}.");

                    value2 = ParseObject();
                    if (value2 == null)
                        throw new ApplicationException($"Dictionary value missing for key {name.Name} at position {name.Position}.");
                    else
                        ThrowOnEmptyOrError(t);

                    // If key already exists then simply overwrite it with latest value
                    entries[name.Name] = new PdfDictEntry() { Name = name, Object = value2 };
                }

                ThrowIfNot<TokenDictionaryClose>(Tokenizer.GetToken());
                return new PdfDictionary(t.Position, entries);
            }
            else if (t is TokenKeyword)
            {
                switch ((t as TokenKeyword).Keyword)
                {
                    case PdfKeyword.True:
                    case PdfKeyword.False:
                        return new PdfBoolean(t as TokenKeyword);
                    case PdfKeyword.Null:
                        return new PdfNull(t as TokenKeyword);
                }
            }

            // Token is not one that starts an object, so put the token back
            Tokenizer.PushToken(t);
            return null;
        }

        protected virtual PdfObject OnResolveReference(PdfObjectReference reference)
        {
            PdfResolveEventArgs args = new PdfResolveEventArgs() { Id = reference.Id, Gen = reference.Gen };
            ResolveReference?.Invoke(this, args);
            return args.Object;
        }

        private T ThrowIfNot<T>(TokenBase t) where T : TokenBase
        {
            if (t is TokenError)
                throw new ApplicationException(t.ToString());
            else if (t is TokenEmpty)
                throw new ApplicationException("Unexpected end of PDF document.");
            else if (!(t is T))
                throw new ApplicationException($"Found {t.GetType().Name} instead of {typeof(T).Name} at position {t.Position}.");

            return (T)t;
        }

        private void ThrowOnError(TokenBase t)
        {
            if (t is TokenError)
                throw new ApplicationException(t.ToString());
        }

        private void ThrowOnEmptyOrError(TokenBase t)
        {
            if (t is TokenError)
                throw new ApplicationException(t.ToString());
            else if (t is TokenEmpty)
                throw new ApplicationException("Unexpected end of PDF document.");
        }

        private Tokenizer Tokenizer { get; set; }
    }
}