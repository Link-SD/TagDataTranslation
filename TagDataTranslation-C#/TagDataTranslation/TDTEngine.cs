﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace TagDataTranslation
{
    public class TDTEngine
    {
        private List<EpcTagDataTranslation> epcTagDataTranslations = new List<EpcTagDataTranslation>();
        private Dictionary<string, int> gcpPrefixLengths = new Dictionary<string, int>();
        private Dictionary<string, Dictionary<int, string>> filterValueTables = new Dictionary<string, Dictionary<int, string>>();

        public TDTEngine()
        {
            //Console.WriteLine("[TagDataTranslation] This library is free for non-commercial use only. Please contact tdt@mimasu.nl for licensing information.");

            var serializer = new XmlSerializer(typeof(EpcTagDataTranslation));

            var assembly = typeof(TagDataTranslation.TDTEngine).GetTypeInfo().Assembly;
            var schemeFilenameStart = "TagDataTranslation.Schemes.";
            var filterFilenameStart = "TagDataTranslation.FilterValueTables.";

            foreach (string filename in assembly.GetManifestResourceNames())
            {
                if (filename.EndsWith(".xml", StringComparison.CurrentCulture) && filename.StartsWith(schemeFilenameStart, StringComparison.CurrentCulture))
                {
                    using (var stream = assembly.GetManifestResourceStream(filename))
                    {
                        using (var reader = XmlReader.Create(stream))
                        {
                            EpcTagDataTranslation epcTagDataTranslation = (EpcTagDataTranslation)serializer.Deserialize(reader);
                            epcTagDataTranslations.Add(epcTagDataTranslation);
                        }
                    }
                }

                if (filename.EndsWith(".csv", StringComparison.CurrentCulture) && filename.StartsWith(filterFilenameStart, StringComparison.CurrentCulture))
                {
                    using (var stream = assembly.GetManifestResourceStream(filename))
                    {
                        using (StreamReader sr = new StreamReader(stream))
                        {
                            string scheme = filename.Split('.')[2].Split('-')[1];

                            filterValueTables.Add(scheme, new Dictionary<int, string>());

                            while (sr.Peek() >= 0)
                            {
                                string line = sr.ReadLine();
                                int filterValue = int.Parse(line.Split(',')[0]);
                                string description = line.Split(',')[1].Trim('"');
                                filterValueTables[scheme].Add(filterValue, description);
                            }
                        }
                    }
                }

                if (filename.EndsWith("gcpprefixformatlist.xml", StringComparison.CurrentCulture))
                {
                    using (var stream = assembly.GetManifestResourceStream(filename))
                    {
                        var xdoc = XDocument.Load(stream);

                        // parse the file into a dictionary
                        gcpPrefixLengths = xdoc.Root.Descendants().ToDictionary(k => (string)k.FirstAttribute.Value, k => int.Parse(k.LastAttribute.Value));
                    }
                }
            }
        }

        public string Translate(string epcIdentifier, string parameterList, string outputFormat)
        {
            return TranslateDetails(epcIdentifier, parameterList, outputFormat).Output;
        }

        public TranslateResult TranslateDetails(string epcIdentifier, string parameterList, string outputFormat)
        {
            var parameterDictionary = ProcessInput(epcIdentifier, parameterList);
            var outputResult = ProcessOutput(parameterDictionary, outputFormat);

            var translateResult = new TranslateResult()
            {
                ParameterDictionary = parameterDictionary,
                Output = outputResult
            };

            return translateResult;
        }

        public Dictionary<string, string> ProcessInput(string epcIdentifier, string parameterList)
        {
            // Translation process according to the TDT standard.

            // 1. SETUP

            // Read the input value and the supplied extra parameters.

            // Populate an associative array of key-value pairs with the supplied extra parameters.
            Dictionary<string, string> parameterDictionary = new Dictionary<string, string>();
            if (parameterList != null)
            {
                try
                {
                    ParseInput(parameterList, parameterDictionary);
                }
                catch (Exception)
                {
                    throw new TDTTranslationException("TDTParameterErrorException");
                }
            }

            // During the translation process, this associative array will be populated with additional
            // values of extracted fields or fields obtained through the application of rules of type
            // 'EXTRACT' or 'FORMAT'

            // 2. DETERMINE THE CODING SCHEME AND INBOUND REPRESENTATION LEVEL.

            // To find the scheme and level that matches the input value, consider all schemes and the
            // prefixMatch attribute of each level element within each scheme.
            Dictionary<Level, Scheme> inputLevelsSchemes = new Dictionary<Level, Scheme>();

            foreach (EpcTagDataTranslation e in epcTagDataTranslations)
            {
                Scheme s = e.scheme[0];

                foreach (Level l in s.level)
                {
                    // If the prefixMatch string matches the input value at the beginning, the scheme and
                    // level should be considered as a candidate for the inbound representation.
                    if (l.prefixMatch == null)
                        continue;

                    if (epcIdentifier.StartsWith(l.prefixMatch, StringComparison.CurrentCulture))
                    {
                        // If the scheme
                        // element specifies a taglength attribute, then if the value of this attribute does not
                        // match the value of the taglength key in the associative array, then this scheme and
                        // level should no longer be considered as a candidate for the inbound representation.
                        if (s.tagLength != null && s.tagLength != "var")
                        {
                            if (parameterDictionary.TryGetValue("taglength", out string taglength))
                            {
                                if (!taglength.Equals(s.tagLength))
                                {
                                    continue;
                                }
                            }
                        }

                        inputLevelsSchemes.Add(l, s);
                    }
                }
            }

            if (inputLevelsSchemes.Count == 0)
            {
                throw new TDTTranslationException("TDTSchemeNotFound");
            }

            // 3. DETERMINE THE OPTION THAT MATCHES THE INPUT VALUE

            // To find the option that matches the input value, consider any scheme+level candidates
            // from the previous step.
            Level inputLevel = null;
            Scheme inputScheme = null;
            Option inputOption = null;

            // Sort by longest prefix matches such that it finds a solution for multiple matches.
            inputLevelsSchemes = inputLevelsSchemes.OrderByDescending(i => i.Key.prefixMatch.Length).ToDictionary(x => x.Key, y => y.Value);

            foreach (KeyValuePair<Level, Scheme> kvp in inputLevelsSchemes)
            {
                Level l = kvp.Key;
                Scheme s = kvp.Value;

                // For each of these schemes, if the optionKey attribute is
                // specified within the scheme element in terms of the name of a supplied parameter (e.g.
                // gs1companyprefixlength), check the associative array of supplied parameters to
                // see if a corresponding value is defined and if so, select the option element for which
                // the optionKey attribute of the option element has the corresponding value.
                //
                // e.g. if a candidate scheme has a scheme attribute
                // optionKey="gs1companyprefixlength" and the associative array of supplied
                // extra parameters has a key=value pair gs1companyprefixlength=7, then only the
                // option element having attribute optionKey="7" should be considered.
                foreach (Option o in l.option)
                {
                    // If the optionKey attribute is not specified within the scheme element or if the
                    // corresponding value is not present in the associative array of supplied extra parameters,
                    // then consider each option element within each scheme+level candidate and check
                    // whether the pattern attribute of the option element matches the input value at the
                    // start of the string.
                    if (s.optionKey != null)
                    {
                        string value;

                        int integer;
                        if (int.TryParse(s.optionKey, out integer))
                        {
                            value = s.optionKey;
                        }
                        else
                        {
                            parameterDictionary.TryGetValue(s.optionKey, out value);
                        }

                        // create exception for empty parameter lists, when no information is necessary to decode
                        if (value != null)
                        {
                            if (o.optionKey != value)
                            {
                                continue;
                            }
                        }
                    }

                    // When a match is found, this option should be considered further and the corresponding
                    // value of the optionKey attribute of the option element should be noted for use in
                    // step 6.

                    // unescape input if Pure Identity or Tag Encoding
                    string epcIdentifierUnescaped;
                    if (l.type == LevelTypeList.PURE_IDENTITY || l.type == LevelTypeList.TAG_ENCODING)
                    {
                        epcIdentifierUnescaped = UnEscape(epcIdentifier);
                    }
                    else
                    {
                        epcIdentifierUnescaped = epcIdentifier;
                    }

                    Regex regex = new Regex("^" + o.pattern);
                    if (regex.IsMatch(epcIdentifierUnescaped))
                    {
                        inputScheme = s;
                        inputLevel = l;
                        inputOption = o;
                        epcIdentifier = epcIdentifierUnescaped;
                        break;
                    }
                }

                if (inputOption != null)
                {
                    break;
                }
            }

            if (inputOption == null)
            {
                throw new TDTTranslationException("TDTOptionNotFound");
            }

            // add option key and scheme name to parameter dictionary
            parameterDictionary.Add("optionkey", inputOption.optionKey);
            parameterDictionary.Add("schemename", inputScheme.name);
            if (!parameterDictionary.ContainsKey("taglength"))
            {
                parameterDictionary.Add("taglength", inputScheme.tagLength);
            }

            // check if all parsing parameters are present
            if (inputLevel.requiredParsingParameters != null)
            {
                foreach (string s in inputLevel.requiredParsingParameters.Split(','))
                {
                    if (!parameterDictionary.ContainsKey(s))
                    {
                        var exception = new TDTTranslationException("TDTUndefinedField");
                        exception.Data.Add("key", s);
                        throw exception;
                    }
                }
            }

            // 4. PARSE THE INPUT VALUE TO EXTRACT VALUES FOR EACH FIELD WITHIN THE OPTION

            // Having found a scheme, level and option matching the input value, consider the field
            // elements nested within the option element.

            // Matching of the input value against the regular expression provided in the pattern
            // attribute of the option element should result in a number of backreference strings being
            // extracted. These should be considered as the values for the field elements, where the
            // seq attribute of the field element indicates the sequence in which the fields are extracted
            // as backreferences, from the start of the input value, e.g. the value from the first
            // backreference should be considered as the value of the field element with seq="1",
            // the value of the second backreference is the value of the field element with seq="2".
            Regex r = new Regex("^" + inputOption.pattern);

            Match m = r.Match(epcIdentifier);

            if (!m.Success)
            {
                throw new TDTTranslationException("TDTEPCIdentifierParseException");
            }

            // sort all fields on seq
            Field[] fields = inputOption.field;
            Field[] fieldsSorted = fields.OrderBy(c => c.seq).ToArray();

            for (int i = 1; i <= fieldsSorted.Length; i++)
            {
                Field inputField = fieldsSorted[i - 1];
                string name = inputField.name;
                string variableElement = m.Groups[i].Value;

                // For each field element, if a characterSet attribute is specified, check that the
                // value of the field falls entirely within the specified character set.
                if (inputField.characterSet != null)
                {
                    if (!ValidateCharacterset(variableElement, inputField.characterSet))
                    {
                        throw new TDTTranslationException("TDTFieldOutsideCharacterSet");
                    }
                }

                // For each field element, if the compaction attribute is null, treat the field as an
                // integer. If the type attribute of the input level was "BINARY", treat the string of 0 and
                // 1 characters matched by the regular expression backreference as a binary string and
                // convert it to a decimal integer.
                if (inputLevel.type == LevelTypeList.BINARY)
                {
                    // If the inbound representation was binary, perform any necessary stripping, conversion of
                    // binary to integer or string, padding, referring to the procedure described in the flowchart
                    // Figure 9b.
                    if (inputField.compactionSpecified)
                    {
                        //TODO: implement check for bitPadChar; somehow not used in TDS1.12.

                        // Convert sequence of bit into characters, 
                        // considering that each byte may have been compacted,
                        // as indicated by the compaction attribute.
                        int compactionBits = 0;
                        switch (inputField.compaction)
                        {
                            case CompactionMethodList.Item5bit:
                                compactionBits = 5;
                                break;
                            case CompactionMethodList.Item6bit:
                                compactionBits = 6;
                                break;
                            case CompactionMethodList.Item7bit:
                                compactionBits = 7;
                                break;
                            case CompactionMethodList.Item8bit:
                                compactionBits = 8;
                                break;
                        }

                        List<byte> byteList = new List<byte>();
                        for (int j = 0; j < variableElement.Length; j += compactionBits)
                        {
                            if (j + compactionBits <= variableElement.Length)
                            {
                                string character = variableElement.Substring(j, compactionBits);
                                // ISO/IEC 15962
                                if (compactionBits == 5)
                                {
                                    // During the decode process, each 5-bit segment of the compacted bit string has “010” added as a prefix to re- create the 8-bit value of the source data.
                                    character = "010" + character;
                                }
                                else if (compactionBits == 6)
                                {
                                    // During the decode process, each 6-bit segment of the compacted bit string is analysed.
                                    // a. If the first bit is “1”, the bits “00” are added as a prefix before converting to values 20 to 3FHEX.
                                    // b. If the first bit is “0”, the bits “01” are added as a prefix before converting to values 40 to 5FHEX.
                                    if (character[0] == '1')
                                    {
                                        character = "00" + character;
                                    }
                                    else
                                    {
                                        character = "01" + character;
                                    }
                                }
                                byteList.Add(Convert.ToByte(character, 2));
                            }
                        }

                        // convert byte list to string
                        variableElement = Encoding.UTF8.GetString(byteList.ToArray(), 0, byteList.Count);

                        // strip null characters at the end of the string
                        variableElement = variableElement.TrimEnd('\0');
                        variableElement = variableElement.TrimEnd('@');
                    }
                    else
                    {
                        //TODO: implement check for bitPadChar; somehow not used in TDS1.6.

                        Int64 integer = Convert.ToInt64(variableElement, 2);
                        variableElement = Convert.ToString(integer);
                    }

                    // Corresponding string field in TAG-ENCODING level
                    Field tagEncodingField = new Field();
                    foreach (Level l in inputScheme.level)
                    {
                        if (l.type.Equals(LevelTypeList.TAG_ENCODING))
                        {
                            foreach (Option o in l.option)
                            {
                                if (o.optionKey.Equals(inputOption.optionKey))
                                {
                                    tagEncodingField = o.field[i - 1];
                                    break;
                                }
                            }
                            break;
                        }
                    }

                    bool padCharInBinary = false;
                    bool padCharInTagEncoding = false;

                    if (inputField.padChar != null)
                    {
                        padCharInBinary = true;
                    }

                    if (tagEncodingField.padChar != null)
                    {
                        padCharInTagEncoding = true;
                    }

                    if (padCharInBinary && padCharInTagEncoding)
                    {
                        // error in TDT definition file
                        throw new TDTTranslationException(@"TDTInvalidDefinitionFile");
                    }

                    if (padCharInBinary && !padCharInTagEncoding)
                    {
                        if (inputField.padDir.Equals(PadDirectionList.LEFT))
                        {
                            variableElement = variableElement.TrimStart(inputField.padChar[0]);
                        }
                        else
                        {
                            variableElement = variableElement.TrimEnd(inputField.padChar[0]);
                        }
                    }

                    if (!padCharInBinary && padCharInTagEncoding)
                    {
                        // Pad at the padDir edge with character indicated by padChar attribute to reach a total length of characters indicated by length attribute
                        if (tagEncodingField.padDir.Equals(PadDirectionList.LEFT))
                        {
                            variableElement = variableElement.PadLeft(int.Parse(tagEncodingField.length), tagEncodingField.padChar[0]);
                        }
                        else
                        {
                            variableElement = variableElement.PadRight(int.Parse(tagEncodingField.length), tagEncodingField.padChar[0]);
                        }

                        // Manage the exception where the length is set to 0.
                        if (int.Parse(tagEncodingField.length) == 0)
                        {
                            variableElement = "";
                        }
                    }
                }

                // If the decimalMinimum attribute is specified, check that the value is not less than the
                // decimal minimum value specified.
                if (inputField.decimalMinimum != null)
                {
                    // only check if the length is larger than zero
                    if (variableElement.Length > 0)
                    {
                        BigInteger integer = BigInteger.Parse(variableElement);
                        if (ValidateMinimum(integer, inputField.decimalMinimum))
                        {
                            throw new TDTTranslationException("TDTFieldBelowMinimum");
                        }
                    }
                }

                // If the decimalMaximum attribute is specified, check that the value is not greater than
                // the decimal maximum value specified.
                if (inputField.decimalMaximum != null)
                {
                    // only check if the length is larger than zero
                    if (variableElement.Length > 0)
                    {
                        BigInteger integer = BigInteger.Parse(variableElement);
                        if (ValidateMaximum(integer, inputField.decimalMaximum))
                        {
                            throw new TDTTranslationException("TDTFieldAboveMaximum");
                        }
                    }
                }

                parameterDictionary[name] = variableElement;
            }

            // 5. PERFORM ANY RULES OF TYPE EXTRACT WITHIN THE INBOUND OPTION IN ORDER TO CALCULATE ADDITIONAL DERIVED FIELDS

            // Now run the rules that have attribute type="EXTRACT" in sequence, to determine any
            // additional derived fields that must be calculated after parsing of the input value.
            if (inputLevel.rule != null)
            {
                Rule[] extractRules = inputLevel.rule;
                Rule[] extractRulesSorted = extractRules.OrderBy(c => c.seq).ToArray();
                ExecuteRules(extractRulesSorted, ModeList.EXTRACT, parameterDictionary);
            }

            return parameterDictionary;
        }

        private string ProcessOutput(Dictionary<string, string> parameterDictionary, string outputFormat)
        {
            // Note the desired outbound level.
            LevelTypeList outputFormatType;
            try
            {
                outputFormatType = (LevelTypeList)Enum.Parse(typeof(LevelTypeList), outputFormat);
            }
            catch (Exception)
            {
                throw new TDTTranslationException("TDTOutputFormatUnknownException");
            }

            // 6. FIND THE CORRESPONDING OPTION IN THE OUTBOUND REPRESENTATION

            // To find the corresponding option in the outbound representation within the same scheme, 
            // select the level element having the desired outbound representation and within that, 
            // select the option element that has the same value of the optionKey attribute that was 
            // noted at the end of step 3
            Scheme outputScheme = null;
            Level outputLevel = null;
            Option outputOption = null;

            foreach (EpcTagDataTranslation e in epcTagDataTranslations)
            {
                Scheme s = e.scheme[0];

                if (s.name != parameterDictionary["schemename"])
                    continue;

                outputScheme = s;

                foreach (Level l in s.level)
                {
                    if (l.type != outputFormatType)
                        continue;

                    outputLevel = l;

                    foreach (Option o in l.option)
                    {
                        if (o.optionKey == parameterDictionary["optionkey"])
                        {
                            outputOption = o;

                        }
                    }
                }
            }

            if (outputLevel == null || outputOption == null)
            {
                throw new TDTTranslationException("TDTOutputNotKnown");
            }

            // check if all formatting parameters are present
            if (outputLevel.requiredFormattingParameters != null)
            {
                foreach (string s in outputLevel.requiredFormattingParameters.Split(','))
                {
                    if (!parameterDictionary.ContainsKey(s))
                    {
                        var exception = new TDTTranslationException("TDTUndefinedField");
                        exception.Data.Add("key", s);
                        throw exception;
                    }
                }
            }

            // 7. PERFORM ANY RULES OF TYPE FORMAT WITHIN THE OUTBOUND REPRESENTATION IN ORDER TO CALCULATE ADDITIONAL DERIVED FIELDS

            // Run any rules with attribute type="FORMAT" in sequence, to determine any additional
            // derived fields that must be calculated in order to prepare the output format.
            //
            // Store the resulting key-value pairs in the associative array after checking that the value 
            // falls entirely within the permitted characterSet (if specified) or within the permitted 
            // numeric range (if decimalMinimum or decimalMaximum are specified) and
            // performing any necessary padding or stripping of characters.
            if (outputLevel.rule != null)
            {
                Rule[] formatRules = outputLevel.rule;
                Rule[] formatRulesSorted = formatRules.OrderBy(c => c.seq).ToArray();
                ExecuteRules(formatRulesSorted, ModeList.FORMAT, parameterDictionary);
            }

            // 8. USE THE GRAMMAR string AND SUBSTITUTIONS FROM THE ASSOCIATIVE ARRAY TO BUILD THE OUTPUT VALUE

            // Consider the grammar string for that option as a sequence of fixed literal strings (the 
            // characters between the single quotes) interspersed with a number of variable elements, 
            // whose key names are indicated by alphanumeric strings without any enclosing single 
            // quotation marks.
            string grammarstring = outputOption.grammar;

            StringBuilder outputString = new StringBuilder();

            Regex abnf = new Regex(@"\'.*?\'|\s*[\w]+\s*");
            MatchCollection collection = abnf.Matches(grammarstring);

            foreach (var c in collection)
            {
                string s = c.ToString();

                if (s[0] == '\'')
                {
                    outputString.Append(s.Substring(1, s.Length - 2));
                }
                else
                {
                    s = s.Trim();

                    // Perform lookups of each key name in the associative array to substitute the value of each 
                    // variable element, substituting the corresponding value in place of the key name.
                    string variableElement;
                    if (!parameterDictionary.TryGetValue(s, out variableElement))
                    {
                        var exception = new TDTTranslationException("TDTUndefinedField");
                        exception.Data.Add("key", s);
                        throw exception;
                    }

                    // Note that if the outbound representation is binary, it is necessary to convert values from 
                    // decimal integer or string to binary, performing any necessary stripping or padding, 
                    // following the method described in the flowchart Figure 9a.
                    if (outputLevel.type == LevelTypeList.BINARY)
                    {
                        // According to flowchart Figure 9a of the standard

                        // Corresponding string field in TAG-ENCODING level
                        Field tagEncodingField = new Field();
                        foreach (Level l in outputScheme.level)
                        {
                            if (l.type.Equals(LevelTypeList.TAG_ENCODING))
                            {
                                foreach (Option o in l.option)
                                {
                                    if (o.optionKey.Equals(parameterDictionary["optionkey"]))
                                    {
                                        foreach (Field f in o.field)
                                        {
                                            if (s.Equals(f.name))
                                            {
                                                tagEncodingField = f;
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                }
                                break;
                            }
                        }

                        // Corresponding string field in BINARY level
                        Field binaryField = new Field();
                        foreach (Field f in outputOption.field)
                        {
                            if (s.Equals(f.name))
                            {
                                binaryField = f;
                                break;
                            }
                        }

                        bool padCharInTagEncoding = false;
                        bool padCharInBinary = false;

                        if (tagEncodingField.padChar != null)
                        {
                            padCharInTagEncoding = true;
                        }

                        if (binaryField.padChar != null)
                        {
                            padCharInBinary = true;
                        }

                        if (padCharInTagEncoding && padCharInBinary)
                        {
                            // error in TDT definition file
                            throw new TDTTranslationException(@"TDTInvalidDefinitionFile");
                        }

                        if (padCharInTagEncoding && !padCharInBinary)
                        {
                            // Strip at the padDir edge of any successive characters indicated by padChar attribute.
                            if (tagEncodingField.padDir.Equals(PadDirectionList.LEFT))
                            {
                                variableElement = variableElement.TrimStart(tagEncodingField.padChar[0]);
                            }
                            else
                            {
                                variableElement = variableElement.TrimEnd(tagEncodingField.padChar[0]);
                            }
                        }

                        if (!padCharInTagEncoding && padCharInBinary)
                        {
                            // Pad at the padDir edge with character indicated by padChar attribute to reach a total length of characters indicated by length attribute
                            if (binaryField.padDir.Equals(PadDirectionList.LEFT))
                            {
                                variableElement = variableElement.PadLeft(int.Parse(binaryField.length), binaryField.padChar[0]);
                            }
                            else
                            {
                                variableElement = variableElement.PadRight(int.Parse(binaryField.length), binaryField.padChar[0]);
                            }
                        }

                        // Check for compaction attribute in BINARY level
                        if (binaryField.compactionSpecified)
                        {
                            // Create an empty buffer for storage of bits.
                            StringBuilder bits = new StringBuilder();

                            // For each character of the string, starting at the left, perform compaction of the corresponding ASCII byte,
                            //byte[] bytes = Encoding.ASCII.GetBytes(variableElement);
                            byte[] bytes = Encoding.UTF8.GetBytes(variableElement);

                            int compactionBits = 0;
                            switch (binaryField.compaction)
                            {
                                case CompactionMethodList.Item5bit:
                                    compactionBits = 5;
                                    break;
                                case CompactionMethodList.Item6bit:
                                    compactionBits = 6;
                                    break;
                                case CompactionMethodList.Item7bit:
                                    compactionBits = 7;
                                    break;
                                case CompactionMethodList.Item8bit:
                                    compactionBits = 8;
                                    break;
                            }

                            // as indicated by the compaction attribute and append the resulting bits to the buffer.
                            // Consider the entire bits in the buffer as a sequence of bits
                            foreach (byte b in bytes)
                            {
                                string binary = Convert.ToString(b, 2);
                                if (binary.Length > compactionBits)
                                {
                                    binary = binary.Substring(binary.Length - compactionBits);
                                }
                                else if (binary.Length < compactionBits)
                                {
                                    binary = binary.PadLeft(compactionBits, '0');
                                }
                                bits.Append(binary);
                            }

                            variableElement = bits.ToString();
                        }
                        else
                        {
                            Int64 result;
                            if (!Int64.TryParse(variableElement, out result))
                            {
                                result = 0;
                            }
                            variableElement = Convert.ToString(result, 2);
                        }

                        // Check for bit padding in BINARY level
                        if (binaryField.bitPadDirSpecified)
                        {
                            if (binaryField.bitPadDir.Equals(PadDirectionList.LEFT))
                            {
                                variableElement = variableElement.PadLeft(int.Parse(binaryField.bitLength), '0');
                            }
                            else
                            {
                                variableElement = variableElement.PadRight(int.Parse(binaryField.bitLength), '0');
                            }
                        }
                    }
                    else
                    {
                        // Validate field
                        foreach (var f in outputOption.field)
                        {
                            if (f.name == s)
                            {
                                if (!ValidateCharacterset(variableElement, f.characterSet))
                                {
                                    throw new TDTTranslationException("TDTFieldOutsideCharacterSet");
                                }
                            }
                        }
                    }

                    if (outputLevel.type == LevelTypeList.PURE_IDENTITY || outputLevel.type == LevelTypeList.TAG_ENCODING)
                    {
                        variableElement = Escape(variableElement);
                    }

                    // Concatenate the fixed literal strings and values of variable together in the sequence
                    // indicated by the grammar string and consider this as the output value.
                    outputString.Append(variableElement);
                }
            }

            return outputString.ToString();
        }

        #region Hex/Binary conversion

        public string HexToBinary(string hex)
        {
            int length = hex.Length;

            StringBuilder binary = new StringBuilder();

            for (int i = 0; i < length; i++)
            {
                int integer = Convert.ToInt16(hex.Substring(i, 1), 16);
                binary.Append(Convert.ToString(integer, 2).PadLeft(4, '0'));
            }

            return binary.ToString();
        }

        public string BinaryToHex(string binary)
        {
            // pad to a multiple of sixteen, according to TDS standard
            int length = binary.Length;
            if (length % 16 != 0)
            {
                int toPad = 16 - length % 16;
                binary = binary.PadRight(length + toPad, '0');
                length = binary.Length;
            }

            StringBuilder hex = new StringBuilder();
            for (int i = 0; i < length; i += 4)
            {
                int integer = Convert.ToInt16(binary.Substring(i, 4), 2);
                hex.Append(Convert.ToString(integer, 16));
            }

            return hex.ToString();
        }

        #endregion

        #region Escaping

        // According to the GS1 General Specifications [GS1GS14.0] for use in alphanumeric serial numbers.
        private string Escape(string input)
        {
            StringBuilder output = new StringBuilder(input);
            output.Replace("%", "%25");
            output.Replace("\"", "%22");
            output.Replace("&", "%26");
            output.Replace("/", "%2F");
            output.Replace("<", "%3C");
            output.Replace(">", "%3E");
            output.Replace("?", "%3F");

            return output.ToString();
        }

        private string UnEscape(string input)
        {
            StringBuilder output = new StringBuilder(input);
            output.Replace("%22", "\"");
            output.Replace("%25", "%");
            output.Replace("%26", "&");
            output.Replace("%2F", "/");
            output.Replace("%3C", "<");
            output.Replace("%3E", ">");
            output.Replace("%3F", "?");

            int c = output.Length;

            return output.ToString();
        }

        #endregion

        #region Rules

        private void ExecuteRules(Rule[] rules, ModeList modeList, Dictionary<string, string> parameterDictionary)
        {
            if (rules == null)
                return;

            foreach (Rule r in rules)
            {
                if (r.type.Equals(modeList))
                {

                    // get new field name
                    string newFieldName = r.newFieldName;

                    // get functionname and parameters
                    string[] functionSplit = r.function.Split(new char[] { '(', ',', ')' }, 128);
                    string functionName = functionSplit[0];
                    string[] functionParameters = new string[functionSplit.Length - 2];
                    Array.Copy(functionSplit, 1, functionParameters, 0, functionSplit.Length - 2);
                    int l = functionParameters.Length;

                    string newFieldValue = default(string);

                    switch (functionName)
                    {
                        case "SUBSTR":
                            if ((l == 2) || (l == 3))
                            {
                                string substrInput = parameterDictionary[functionParameters[0]];
                                int offset = int.Parse(GetValue(functionParameters[1], parameterDictionary));

                                if (l == 2)
                                {
                                    newFieldValue = RuleSUBSTR(substrInput, offset);
                                }
                                else
                                {
                                    int length = int.Parse(GetValue(functionParameters[2], parameterDictionary));
                                    newFieldValue = RuleSUBSTR(substrInput, offset, length);
                                }
                            }
                            break;
                        case "CONCAT":
                            List<string> concatInput = new List<string>();
                            for (int i = 0; i < l; i++)
                            {
                                string s = GetValue(functionParameters[i], parameterDictionary);
                                concatInput.Add(s);
                            }
                            newFieldValue = RuleCONCAT(concatInput);
                            break;
                        case "GS1CHECKSUM":
                            string value = GetValue(functionParameters[0], parameterDictionary);
                            newFieldValue = RuleGS1CHECKSUM(value);
                            break;
                        // TODO: Implement additional functions.
                        default:
                            throw new TDTTranslationException("TDTNotImplementedException");
                    }

                    // Store the resulting key-value pairs in the associative array after checking that the value
                    // falls entirely within the permitted characterSet (if specified) 
                    if (r.characterSet != null)
                    {
                        if (!ValidateCharacterset(newFieldValue, r.characterSet))
                        {
                            throw new TDTTranslationException("TDTFieldOutsideCharacterSet");
                        }
                    }

                    // or within the permitted 
                    // numeric range (if decimalMinimum or decimalMaximum are specified) 
                    if (r.decimalMinimum != null)
                    {
                        BigInteger integer = BigInteger.Parse(newFieldValue);
                        if (ValidateMinimum(integer, r.decimalMinimum))
                        {
                            throw new TDTTranslationException("TDTFieldBelowMinimum");
                        }
                    }

                    if (r.decimalMaximum != null)
                    {
                        BigInteger integer = BigInteger.Parse(newFieldValue);
                        if (ValidateMaximum(integer, r.decimalMaximum))
                        {
                            throw new TDTTranslationException("TDTFieldAboveMaximum");
                        }
                    }

                    // and performing any necessary padding or stripping of characters.
                    //TODO: Implement padding or stripping of characters.

                    parameterDictionary[newFieldName] = newFieldValue;
                }
            }
        }

        private string GetValue(string input, Dictionary<string, string> epcIdentifierDictionary)
        {
            // Check if the field is an integer and if so, return it
            int result;
            if (int.TryParse(input, out result))
            {
                return input;
            }

            // If it is not an integer, it is a fieldname that should be retrieved seperately
            string newInput = epcIdentifierDictionary[input];
            return newInput;
        }

        // SUBSTR (string, offset)
        // the substring starting at <offset> (offset=0 is the first character of string)
        private string RuleSUBSTR(string input, int offset)
        {
            return input.Substring(offset);
        }

        // SUBSTR (string, offset, length)
        // the substring starting at <offset> (offset=0 is the first character of string) and of <length> characters
        private string RuleSUBSTR(string input, int offset, int length)
        {
            return input.Substring(offset, length);
        }

        // CONCAT (string1, string2, string3,...)
        // concatenation of string parameters
        private string RuleCONCAT(List<string> input)
        {
            return string.Concat(input);
        }

        // GS1CHECKSUM (string)
        // Computes the GS1 checksum digit given a string containing all the preceding digits
        private string RuleGS1CHECKSUM(string input)
        {
            // courtesy of http://codereview.stackexchange.com/questions/126685/calculate-gs1-sscc-upc-check-digit
            int sum = 0;
            for (int i = 0; i < input.Length; i++)
            {
                int n = int.Parse(input.Substring(input.Length - 1 - i, 1));
                sum += i % 2 == 0 ? n * 3 : n;
            }

            int result = sum % 10 == 0 ? 0 : 10 - sum % 10;
            return result.ToString();
        }

        #endregion

        #region Validation

        private bool ValidateCharacterset(string input, string characterSet)
        {
            if (input.Length == 0)
                return true;

            Regex r = new Regex(characterSet.TrimEnd('*'));
            return r.IsMatch(input);
        }

        private bool ValidateMinimum(BigInteger input, string minimum)
        {
            BigInteger minimumInt;
            BigInteger.TryParse(minimum, out minimumInt);
            return input < minimumInt;
        }

        private bool ValidateMaximum(BigInteger input, string maximum)
        {
            BigInteger maximumInt;
            BigInteger.TryParse(maximum, out maximumInt);
            return input > maximumInt;
        }

        #endregion

        #region Helper

        private void ParseInput(string input, Dictionary<string, string> parameterDictionary)
        {
            if (input.Length > 0)
            {
                foreach (string s in input.Split(';'))
                {
                    parameterDictionary.Add(s.Split('=')[0].Trim().ToLower(), s.Split('=')[1].Trim().ToLower());
                }
            }
        }

        public PrefixLengthResult GetPrefixLength(string input)
        {
            var prefixLength = gcpPrefixLengths.Where(x => input.StartsWith(x.Key, StringComparison.CurrentCulture)).FirstOrDefault();

            return new PrefixLengthResult() { Prefix = prefixLength.Key, Length = prefixLength.Value };
        }

        public Dictionary<int, string> GetFilterValueTable(string scheme)
        {
            var filterValueTable = new Dictionary<int, string>();

            scheme = scheme.ToUpper();

            if (filterValueTables.ContainsKey(scheme))
            {
                filterValueTable = filterValueTables[scheme];
            }

            return filterValueTable;
        }

        #endregion
    }
}