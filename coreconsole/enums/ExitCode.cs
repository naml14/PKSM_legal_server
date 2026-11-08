namespace coreconsole.enums;

public enum ExitCode: int
{
    Success = 0,
    BadBase64 = 1,
    Base64NotPokemon = 2,
    UnknownErrorDuringBase64ToPokemon = 3,
    EnvNotConfigured = 4,
    // The command line could not be understood (unknown option value, missing argument, ...).
    // Kept separate from the other codes so the existing 1/2/3/4 meanings stay untouched.
    InvalidCommandLine = 5
}