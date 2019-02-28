﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sharpmake
{
    /// <summary>
    /// IParsingFlowParser classes must also implement ISourceParser.
    /// Given the lines of a file, they can decide whether the other parsers should parse the current line or not. 
    /// </summary>
    public interface IParsingFlowParser : ISourceParser
    {
        /// <summary>
        /// Returns true if the line should be parsed by the other source parsers.
        /// </summary>
        bool ShouldParseLine();

        /// <summary>
        /// Called when a file is being parsed
        /// </summary>
        void FileParsingBegin(string file);

        /// <summary>
        /// Called when a file is done being parsed
        /// </summary>
        void FileParsingEnd(string file);
    }

    /// <summary>
    /// The parsing flow parser that parses the #if, #elif, #else inside the Sharpmake files
    /// Depending if the current block is defined (lets say #if SYMBOL, where SYMBOL has been passed in the application's parameters)
    /// This parser is used to prevent parsing Includes and References from blocks are not defined.
    /// </summary>
    public class PreprocessorConditionParser : IParsingFlowParser
    {

        // These regexes DO NOT support complex expressions like #if (A || !B)
        private static readonly RegexOptions _regexOptions = RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant;
        private static readonly Regex _ifRegex = new Regex(@"^\s*#if\s+(?<symbolName>\w+)", _regexOptions);
        private static readonly Regex _elifRegex = new Regex(@"^\s*#elif\s+(?<symbolName>\w+)", _regexOptions);
        private static readonly Regex _elseRegex = new Regex(@"^\s*#else", _regexOptions);
        private static readonly Regex _endifRegex = new Regex(@"^\s*#endif", _regexOptions);

        // It is possible that the parser might be called from different threads, the state of the parser must be per thread so that we don't mix files.
        private ThreadLocal<State> _state = new ThreadLocal<State>(() => new State());
        private readonly HashSet<string> _defines;

        public PreprocessorConditionParser(HashSet<string> defines)
        {
            if (defines == null)
                throw new ArgumentNullException(nameof(defines));
            _defines = defines;
        }

        public void ParseLine(string line, FileInfo sourceFilePath, int lineNumber, IAssemblerContext context)
        {
            string trimmedLine = line.TrimStart();
            if (!trimmedLine.StartsWith("#"))
                return;

            // Are we in a #if block ?
            Match match = _ifRegex.Match(trimmedLine);
            if (match.Success)
            {
                PushNewConditionBlock();
                TestConditionBlockBranch(match.Groups["symbolName"].Value);
                return;
            }

            // Are we in a #elif block ?
            match = _elifRegex.Match(trimmedLine);
            if (match.Success)
            {
                TestConditionBlockBranch(match.Groups["symbolName"].Value);
                return;
            }

            // Are we in a #else block ?
            match = _elseRegex.Match(trimmedLine);
            if (match.Success)
            {
                TestConditionBlockBranch(null);
                return;
            }

            // Are we in a #endif block ?
            match = _endifRegex.Match(trimmedLine);
            if (match.Success)
            {
                PopConditionBlock();
                return;
            }
        }


        private void PushNewConditionBlock()
        {
            _state.Value.NestedConditionBlocks.Push(new ConditionBlock());
        }

        private void TestConditionBlockBranch(string symbolName)
        {
            State state = _state.Value;
            if (!state.NestedConditionBlocks.Any())
                throw new Error("invalid condition block format for symbol: {0}", symbolName);

            ConditionBlock topBlock = state.NestedConditionBlocks.Peek();
            topBlock.CurrentDefine = symbolName;
            topBlock.Defined = false;

            // If one branch of the 'if' has been resolved, than don't evaluate other branches
            if (!topBlock.Resolved)
            {
                topBlock.Defined = symbolName == null || _defines.Contains(symbolName);
                topBlock.Resolved = topBlock.Defined;
            }

            // The current line being parsed is defined if all nested 'if' blocks are defined.
            state.IsCurrentCodeBlockDefined = state.NestedConditionBlocks.All(d => d.Defined);
        }

        private void PopConditionBlock()
        {
            State state = _state.Value;
            if (!state.NestedConditionBlocks.Any())
                throw new Error("invalid condition block format");

            state.NestedConditionBlocks.Pop();

            // The current line being parsed is defined if all nested 'if' blocks are defined or if we are not in any 'if' block.
            state.IsCurrentCodeBlockDefined = !state.NestedConditionBlocks.Any();
        }

        public bool ShouldParseLine()
        {

            return _state.Value.IsCurrentCodeBlockDefined;
        }

        public void FileParsingBegin(string file)
        {
            // No logic here yet
        }

        public void FileParsingEnd(string file)
        {
            // Basic validations so that we can catch early malformed Sharpmake files
            if (_state.Value.NestedConditionBlocks.Any())
                throw new Exception($"End of file reached before all nested condition blocks (#if, #else, ...) have been closed, malformed Sharpmake {file}");
        }

        private class State
        {
            /// <summary>
            /// All nested condition blocks parsed so far (#if inside #if)
            /// </summary>
            public Stack<ConditionBlock> NestedConditionBlocks { get; } = new Stack<ConditionBlock>();

            /// <summary>
            /// The current code block is defined if we are currently parsing a line that is inside a block where all nested conditions are defined.
            /// </summary>
            public bool IsCurrentCodeBlockDefined { get; set; } = true;

        }

        private class ConditionBlock
        {
            /// <summary>
            /// Did at least one branch of the (if, elif, else) branches has been resolved (did we entered one of the block) ?
            /// </summary>
            public bool Resolved { get; set; } = false;

            /// <summary>
            /// Is the current block of code being defined (condition evaluated as true) ?
            /// </summary>
            public bool Defined { get; set; } = false;

            /// <summary>
            /// Name of the define being tested.
            /// </summary>
            public string CurrentDefine { get; set; } = "";
        }
    }

}
