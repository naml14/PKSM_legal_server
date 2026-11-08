package pksmhandler

import (
	"bytes"
	"context"
	"encoding/json"
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
func newPkmnRequest(t *testing.T, path string, content []byte, includePkmn bool) *http.Request {
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

	if err := writer.Close(); err != nil {
		t.Fatalf("failed to close multipart writer: %v", err)
	}

	request := httptest.NewRequest(http.MethodPost, path, &body)
	request.Header.Set("Content-Type", writer.FormDataContentType())

	return request
}

func newRouter() chi.Router {
	router := chi.NewRouter()
	router.Route("/api/v2/pksm", NewHandler().Route)

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

func TestLegalityMissingGenerationHeader(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, "/api/v2/pksm/legality", []byte("pokemon-bytes"), true))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "version header is required" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityInvalidGeneration(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legality", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen42")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "invalid generation" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityGenerationVariants(t *testing.T) {
	cases := map[string]string{
		"Gen8b": "Gen8b",
		"gen9":  "Gen9",
		"GEN7":  "Gen7",
		" 9 ":   "Gen9",
	}

	for sent, expected := range cases {
		calls := stubCoreConsole(t, `{"legal":true,"report":[]}`, nil)

		request := newPkmnRequest(t, "/api/v2/pksm/legality", []byte("pokemon-bytes"), true)
		request.Header.Set(GenerationHeader, sent)

		recorder := httptest.NewRecorder()
		newRouter().ServeHTTP(recorder, request)

		if recorder.Code != http.StatusOK {
			t.Fatalf("%q: expected status %d, got %d (%s)", sent, http.StatusOK, recorder.Code, recorder.Body.String())
		}

		if len(*calls) != 1 {
			t.Fatalf("%q: expected one coreconsole call, got %d", sent, len(*calls))
		}

		want := "--generation=" + expected
		if (*calls)[0].extraArgs[0] != want {
			t.Fatalf("%q: expected %q, got %q", sent, want, (*calls)[0].extraArgs[0])
		}
	}
}

func TestLegalityMissingPkmn(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legality", nil, false)
	request.Header.Set(GenerationHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "no pokemon file provided" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityInvalidMultipart(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := httptest.NewRequest(http.MethodPost, "/api/v2/pksm/legality", bytes.NewReader([]byte("not multipart")))
	request.Header.Set(GenerationHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "invalid multipart form" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalityFileTooLarge(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legality", bytes.Repeat([]byte("a"), 3*1024*1024), true)
	request.Header.Set(GenerationHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("expected status %d, got %d", http.StatusRequestEntityTooLarge, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "file too large" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalitySuccess(t *testing.T) {
	calls := stubCoreConsole(t, `{"legal":false,"report":["Bad checksum"]}`, nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legality", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

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

	found := false
	for _, arg := range (*calls)[0].extraArgs {
		if arg == "--generation=Gen9" {
			found = true
		}
	}

	if !found {
		t.Fatalf("expected the generation to be forwarded, got %v", (*calls)[0].extraArgs)
	}
}

func TestLegalizeMissingGenerationHeader(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(VersionHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "version header is required" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalizeMissingVersionHeader(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "version header is required" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalizeAlreadyLegal(t *testing.T) {
	stubCoreConsole(t, `{"error": "this pokemon is already legal!"}`, nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")
	request.Header.Set(VersionHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "this pokemon is already legal!" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestLegalizeReturnsTheChangesOfTheEngine(t *testing.T) {
	stubCoreConsole(t, `{"legal":true,"report":["Legal!"],"pokemon":"BASE64","changes":["met date 1/1/1 -> 2017-11-17","move 1 PP 0 -> 35"]}`, nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")
	request.Header.Set(VersionHeader, "SV")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload struct {
		Changes []string `json:"changes"`
	}
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	if len(payload.Changes) != 2 {
		t.Fatalf("expected the 2 changes of the engine, got %v", payload.Changes)
	}

	if payload.Changes[0] != "met date 1/1/1 -> 2017-11-17" {
		t.Fatalf("unexpected change %q", payload.Changes[0])
	}
}

func TestLegalizeSuccess(t *testing.T) {
	calls := stubCoreConsole(t, `{"legal":true,"report":["ok"],"pokemon":"BASE64"}`, nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")
	request.Header.Set(VersionHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	if payload["legal"] != true {
		t.Fatalf("expected legal to be true, got %v", payload["legal"])
	}

	if payload["success"] != true {
		t.Fatalf("expected success to be true, got %v", payload["success"])
	}

	if payload["ran"] != true {
		t.Fatalf("expected ran to be true, got %v", payload["ran"])
	}

	if payload["pokemon"] != "BASE64" {
		t.Fatalf("expected the pokemon to be returned, got %v", payload["pokemon"])
	}

	if _, ok := payload["report"]; !ok {
		t.Fatalf("expected the report field in %q", recorder.Body.String())
	}

	if len(*calls) != 1 || (*calls)[0].mode != "legalize" {
		t.Fatalf("expected a single legalize call, got %v", *calls)
	}

	expectedArgs := map[string]bool{"--generation=Gen9": false, "--version=Gen9": false}
	for _, arg := range (*calls)[0].extraArgs {
		if _, ok := expectedArgs[arg]; ok {
			expectedArgs[arg] = true
		}
	}

	for arg, found := range expectedArgs {
		if !found {
			t.Fatalf("expected %s to be forwarded, got %v", arg, (*calls)[0].extraArgs)
		}
	}
}

func TestLegalizeWithoutResult(t *testing.T) {
	stubCoreConsole(t, `{"legal":false,"report":["could not legalize"],"pokemon":null}`, nil)

	request := newPkmnRequest(t, "/api/v2/pksm/legalize", []byte("pokemon-bytes"), true)
	request.Header.Set(GenerationHeader, "Gen9")
	request.Header.Set(VersionHeader, "Gen9")

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	if payload["success"] != false {
		t.Fatalf("expected success to be false, got %v", payload["success"])
	}

	if payload["ran"] != true {
		t.Fatalf("expected ran to be true, got %v", payload["ran"])
	}

	if payload["pokemon"] != nil {
		t.Fatalf("expected a null pokemon, got %v", payload["pokemon"])
	}
}
