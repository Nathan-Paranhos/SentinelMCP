namespace Sentinel.Core.Models;

public enum DecisionStatus
{
    Proposed,
    Active,
    Superseded,
    Rejected
}

public enum IncidentStatus
{
    Reported
}

public enum UserRole
{
    Reader,
    Contributor,
    Maintainer
}

public enum SearchMode
{
    Lexical,
    Hybrid
}

public enum MatchStatus
{
    Matched,
    NoMatch
}
