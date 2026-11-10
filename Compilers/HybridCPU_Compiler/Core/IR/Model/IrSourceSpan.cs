using System;

namespace HybridCPU.Compiler.Core.IR
{
    /// <summary>
    /// Source location span for parser-oriented IR metadata and diagnostics.
    /// </summary>
    public sealed record IrSourceSpan
    {
        public IrSourceSpan(
            string documentName,
            int startLine,
            int startColumn,
            int endLine,
            int endColumn,
            int startOffset,
            int length)
        {
            ArgumentNullException.ThrowIfNull(documentName);
            if (string.IsNullOrWhiteSpace(documentName))
            {
                throw new ArgumentException("Document name cannot be empty or whitespace.", nameof(documentName));
            }

            if (startLine < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(startLine), "Start line must be positive.");
            }

            if (startColumn < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(startColumn), "Start column must be positive.");
            }

            if (endLine < startLine)
            {
                throw new ArgumentOutOfRangeException(nameof(endLine), "End line must be greater than or equal to start line.");
            }

            if (endColumn < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(endColumn), "End column must be positive.");
            }

            if (endLine == startLine && endColumn < startColumn)
            {
                throw new ArgumentOutOfRangeException(nameof(endColumn), "End column must be greater than or equal to start column on the same line.");
            }

            if (startOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startOffset), "Start offset cannot be negative.");
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
            }

            DocumentName = documentName;
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
            StartOffset = startOffset;
            Length = length;
        }

        public string DocumentName { get; }

        public int StartLine { get; }

        public int StartColumn { get; }

        public int EndLine { get; }

        public int EndColumn { get; }

        public int StartOffset { get; }

        public int Length { get; }

        public int EndOffset => checked(StartOffset + Length);
    }
}
