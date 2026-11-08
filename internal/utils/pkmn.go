package utils

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"

	"github.com/getsentry/sentry-go"
	"github.com/lrstanley/chix"
)

const (
	// PkmnFormField is the name of the multipart form field that carries the
	// pokemon file (the raw bytes of a .pk9 file).
	PkmnFormField = "pkmn"

	// DefaultPkmnMaxSize is the maximum request body size accepted by the
	// legacy endpoints (/api/info, /api/legality/check, /api/legality/legalize).
	DefaultPkmnMaxSize int64 = 1024

	// PksmPkmnMaxSize is the maximum request body size accepted by the v2
	// endpoints (/api/v2/pksm/legality, /api/v2/pksm/legalize). It matches the
	// 2 MB limit used by FlagBrew/local-gpss.
	PksmPkmnMaxSize int64 = 2 * 1024 * 1024
)

// Error taxonomy for pokemon extraction failures. Handlers must never leak the
// underlying Go error text to the client.
var (
	// ErrFileTooLarge is returned when the request body exceeds the allowed size.
	ErrFileTooLarge = errors.New("file too large")
	// ErrMissingFile is returned when the pkmn form field is missing or empty.
	ErrMissingFile = errors.New("no pokemon file provided")
	// ErrInvalidForm is returned when the request body is not a valid multipart form.
	ErrInvalidForm = errors.New("invalid multipart form")
)

// GetPkmnFromRequest extracts the pokemon file from a multipart request and
// returns it base64 encoded. The legacy request size limit applies.
func GetPkmnFromRequest(w http.ResponseWriter, r *http.Request) (string, error) {
	return GetPkmnFromRequestWithLimit(w, r, DefaultPkmnMaxSize)
}

// GetPkmnFromRequestWithLimit extracts the pokemon file from a multipart
// request and returns it base64 encoded, refusing bodies larger than maxSize.
func GetPkmnFromRequestWithLimit(w http.ResponseWriter, r *http.Request, maxSize int64) (string, error) {
	if r.ContentLength > maxSize {
		return "", ErrFileTooLarge
	}

	r.Body = http.MaxBytesReader(w, r.Body, maxSize)
	if err := r.ParseMultipartForm(500 * 1024); err != nil {
		if isBodyTooLarge(err) {
			return "", ErrFileTooLarge
		}

		return "", ErrInvalidForm
	}

	file, _, err := r.FormFile(PkmnFormField)
	if err != nil {
		if errors.Is(err, http.ErrMissingFile) {
			return "", ErrMissingFile
		}

		return "", ErrInvalidForm
	}

	defer file.Close()

	// Copy the file to a byte array
	var buf bytes.Buffer

	if _, err := io.Copy(&buf, file); err != nil {
		if isBodyTooLarge(err) {
			return "", ErrFileTooLarge
		}

		return "", ErrInvalidForm
	}

	if buf.Len() == 0 {
		return "", ErrMissingFile
	}

	// Convert the file to base64
	return base64.StdEncoding.EncodeToString(buf.Bytes()), nil
}

// isBodyTooLarge reports whether the given error was caused by the request
// body exceeding the limit enforced by http.MaxBytesReader.
func isBodyTooLarge(err error) bool {
	var maxBytesError *http.MaxBytesError
	if errors.As(err, &maxBytesError) {
		return true
	}

	return strings.Contains(err.Error(), "request body too large")
}

// RespondPkmnError writes the HTTP response matching the given pokemon
// extraction error. It reports whether the error was handled.
func RespondPkmnError(w http.ResponseWriter, r *http.Request, err error) bool {
	switch {
	case errors.Is(err, ErrFileTooLarge):
		chix.JSON(w, r, http.StatusRequestEntityTooLarge, chix.M{
			"error": "file too large",
		})
	case errors.Is(err, ErrMissingFile):
		chix.JSON(w, r, http.StatusBadRequest, chix.M{
			"error": "no pokemon file provided",
		})
	case errors.Is(err, ErrInvalidForm):
		chix.JSON(w, r, http.StatusBadRequest, chix.M{
			"error": "invalid multipart form",
		})
	default:
		return false
	}

	return true
}

// RespondCoreConsoleError maps a failed coreconsole invocation to an HTTP
// response. The raw coreconsole output is returned as-is for the invalid
// pokemon exit codes so the client sees the original error body.
func RespondCoreConsoleError(w http.ResponseWriter, r *http.Request, output string, err error) {
	switch err.Error() {
	case "CoreConsole exited with code 1", "CoreConsole exited with code 2":
		// The provided pkmn is invalid.
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)

		_, _ = w.Write([]byte(output))
	case "CoreConsole exited with code 3":
		// Something unknown happened, but should've been captured by sentry inside coreconsole itself.
		chix.Error(w, r, fmt.Errorf("something went wrong, please try again later"))
	case "CoreConsole exited with code 4":
		// The .env is missing for coreconsole, which should only happen if it isn't set up yet.
		chix.JSON(w, r, http.StatusServiceUnavailable, chix.M{
			"error": "CoreConsole is currently being set-up, please try again later",
		})
	default:
		// If we got here, then something went wrong but likely wasn't caught by coreconsole inside sentry so we'll deal with it here.
		sentry.CaptureException(err)
		chix.Error(w, r, fmt.Errorf("something went wrong, please try again later"))
	}
}

// CoreConsoleErrorMessage returns the message of the "error" key contained in
// a coreconsole JSON response, if there is one. Coreconsole reports some
// failures (for example a pokemon that is already legal) with a successful
// exit code and an error object in the payload.
func CoreConsoleErrorMessage(output string) (string, bool) {
	var payload map[string]json.RawMessage
	if err := json.Unmarshal([]byte(output), &payload); err != nil {
		return "", false
	}

	raw, ok := payload["error"]
	if !ok {
		return "", false
	}

	var message string
	if err := json.Unmarshal(raw, &message); err != nil {
		return "", false
	}

	return message, true
}
