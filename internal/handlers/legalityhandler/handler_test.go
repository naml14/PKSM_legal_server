package legalityhandler

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/go-chi/chi/v5"
)

type coreConsoleCall struct {
	mode      string
	pokemon   string
	extraArgs []string
}

// stubCoreConsole replaces the coreconsole invocation for the duration of a test.
func stubCoreConsole(t *testing.T, output string, err error) *[]coreConsoleCall {
	t.Helper()

	var calls []coreConsoleCall
	original := runCoreConsole
	runCoreConsole = func(ctx context.Context, mode, pokemon string, extraArgs ...string) (string, error) {
		calls = append(calls, coreConsoleCall{mode: mode, pokemon: pokemon, extraArgs: extraArgs})
		return output, err
	}

	t.Cleanup(func() {
		runCoreConsole = original
	})

	return &calls
}

// newPkmnRequest builds a multipart request carrying the given pokemon bytes.
// Additional form fields can be provided as key/value pairs.
func newPkmnRequest(t *testing.T, path string, content []byte, includePkmn bool, fields ...string) *http.Request {
	t.Helper()

	var body bytes.Buffer
	writer := multipart.NewWriter(&body)

	if includePkmn {
		file, err := writer.CreateFormFile("pkmn", "pokemon.pk9")
		if err != nil {
			t.Fatalf("failed to create form file: %v", err)
		}

		if _, err := file.Write(content); err != nil {
			t.Fatalf("failed to write form file: %v", err)
		}
	}

	for i := 0; i+1 < len(fields); i += 2 {
		if err := writer.WriteField(fields[i], fields[i+1]); err != nil {
			t.Fatalf("failed to write form field: %v", err)
		}
	}

	if err := writer.Close(); err != nil {
		t.Fatalf("failed to close multipart writer: %v", err)
	}

	request := httptest.NewRequest(http.MethodPost, path, &body)
	request.Header.Set("Content-Type", writer.FormDataContentType())

	return request
}

func newRouter() chi.Router {
	router := chi.NewRouter()
	router.Route("/api/legality", NewHandler().Route)

	return router
}

func decodeError(t *testing.T, recorder *httptest.ResponseRecorder) string {
	t.Helper()

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	message, ok := payload["error"].(string)
	if !ok {
		t.Fatalf("expected an error field, got %q", recorder.Body.String())
	}

	return message
}

func TestLegalityCheckMissingPkmn(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/check", nil, false))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "no pokemon file provided" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityCheckInvalidMultipart(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := httptest.NewRequest(http.MethodPost, "/api/legality/check", bytes.NewReader([]byte("not multipart")))
	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "invalid multipart form" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityCheckFileTooLarge(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/check", bytes.Repeat([]byte("a"), 2048), true))

	if recorder.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("expected status %d, got %d", http.StatusRequestEntityTooLarge, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "file too large" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityCheckSuccess(t *testing.T) {
	calls := stubCoreConsole(t, `{"legal":false,"report":["Bad checksum"]}`, nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/check", []byte("pokemon-bytes"), true))

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	if payload["legal"] != false {
		t.Fatalf("expected legal to be false, got %v", payload["legal"])
	}

	report, ok := payload["report"].([]any)
	if !ok || len(report) != 1 || report[0] != "Bad checksum" {
		t.Fatalf("unexpected report %v", payload["report"])
	}

	if len(*calls) != 1 || (*calls)[0].mode != "legality" {
		t.Fatalf("expected a single legality call, got %v", *calls)
	}
}

func TestLegalityCheckInvalidPokemon(t *testing.T) {
	stubCoreConsole(t, `{"error": "base64 is not a pokemon"}`, errors.New("CoreConsole exited with code 2"))

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/check", []byte("not a pokemon"), true))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "base64 is not a pokemon" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalizeSuccess(t *testing.T) {
	calls := stubCoreConsole(t, `{"legal":true,"report":["ok"],"pokemon":"BASE64"}`, nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/legalize", []byte("pokemon-bytes"), true))

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	for _, field := range []string{"legal", "report", "pokemon"} {
		if _, ok := payload[field]; !ok {
			t.Fatalf("expected the %q field in %q", field, recorder.Body.String())
		}
	}

	if len(*calls) != 1 || (*calls)[0].mode != "legalize" {
		t.Fatalf("expected a single legalize call, got %v", *calls)
	}
}

func TestLegalizeAlreadyLegal(t *testing.T) {
	stubCoreConsole(t, `{"error": "this pokemon is already legal!"}`, nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/legality/legalize", []byte("pokemon-bytes"), true))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "this pokemon is already legal!" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityCheckLegacyGenerationIsStillValidated(t *testing.T) {
	// The legacy routes keep validating the generation against the legacy enum,
	// which does not know about Gen9. The v2 routes read the generation from
	// the header instead.
	calls := stubCoreConsole(t, `{"legal":true,"report":[]}`, nil)

	request := newPkmnRequest(t, "/api/legality/check", []byte("pokemon-bytes"), true, "generation", "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if len(*calls) != 0 {
		t.Fatalf("expected coreconsole to not be called, got %v", *calls)
	}
}

func TestLegalizeForcedGenerationIsForwarded(t *testing.T) {
	calls := stubCoreConsole(t, `{"legal":true,"report":["ok"],"pokemon":"BASE64"}`, nil)

	request := newPkmnRequest(t, "/api/legality/legalize", []byte("pokemon-bytes"), true, "forced_generation", "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	if len(*calls) != 1 {
		t.Fatalf("expected a single call, got %v", *calls)
	}

	found := false
	for _, arg := range (*calls)[0].extraArgs {
		if arg == "--legalization-generation=Gen9" {
			found = true
		}
	}

	if !found {
		t.Fatalf("expected the forced generation to be forwarded, got %v", (*calls)[0].extraArgs)
	}
}
