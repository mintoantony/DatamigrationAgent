using Dbm.Core.Matching;

namespace Dbm.Core.State;

public sealed record VectorRow(Side Side, string Kind, string Key, string Text, SparseVector Vec);   // Kind "table"|"column"
