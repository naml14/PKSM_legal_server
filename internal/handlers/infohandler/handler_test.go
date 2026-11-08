package infohandler

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

// stubCoreConsole replaces the coreconsole invocation for the duration of a test.
func stubCoreConsole(t *testing.T, output string, err error) *[]string {
	t.Helper()

	var calls []string
	original := runCoreConsole
	runCoreConsole = func(ctx context.Context, mode, pokemon string, extraArgs ...string) (string, error) {
		calls = append(calls, mode)
		return output, err
	}

	t.Cleanup(func() {
		runCoreConsole = original
	})

	return &calls
}

// newPkmnRequest builds a multipart request carrying the given pokemon bytes.
func newPkmnRequest(t *testing.T, content []byte, includePkmn bool) *http.Request {
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

	request := httptest.NewRequest(http.MethodPost, "/api/info", &body)
	request.Header.Set("Content-Type", writer.FormDataContentType())

	return request
}

func newRouter() chi.Router {
	router := chi.NewRouter()
	router.Route("/api/info", NewHandler().Route)

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

func TestGetInfoMissingPkmn(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, nil, false))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "no pokemon file provided" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestGetInfoInvalidMultipart(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	request := httptest.NewRequest(http.MethodPost, "/api/info", bytes.NewReader([]byte("not multipart")))
	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, request)

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "invalid multipart form" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestGetInfoFileTooLarge(t *testing.T) {
	stubCoreConsole(t, "{}", nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, bytes.Repeat([]byte("a"), 2048), true))

	if recorder.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("expected status %d, got %d", http.StatusRequestEntityTooLarge, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "file too large" {
		t.Fatalf("unexpected error %q", message)
	}
}

func TestGetInfoSuccess(t *testing.T) {
	calls := stubCoreConsole(t, `{"ot":"Aipom","sprites":{},"is_legal":true,"illegal_reasons":[],"base64":"AAAA"}`, nil)

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, []byte("pokemon-bytes"), true))

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, recorder.Code)
	}

	var payload map[string]any
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
	}

	for _, field := range []string{"ot", "sprites", "is_legal", "illegal_reasons", "base64"} {
		if _, ok := payload[field]; !ok {
			t.Fatalf("expected the %q field in %q", field, recorder.Body.String())
		}
	}

	if len(*calls) != 1 || (*calls)[0] != "summary" {
		t.Fatalf("expected a single summary call, got %v", *calls)
	}
}

func TestGetInfoInvalidPokemon(t *testing.T) {
	stubCoreConsole(t, `{"error": "base64 is not a pokemon"}`, errors.New("CoreConsole exited with code 2"))

	recorder := httptest.NewRecorder()
	newRouter().ServeHTTP(recorder, newPkmnRequest(t, []byte("not a pokemon"), true))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	if message := decodeError(t, recorder); message != "base64 is not a pokemon" {
		t.Fatalf("unexpected error %q", message)
	}
}
