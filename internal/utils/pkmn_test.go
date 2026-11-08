package utils

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"testing"
)

// newMultipartRequest builds a multipart request with the given form field. When
// includePkmn is false the "pkmn" field is omitted entirely.
func newMultipartRequest(t *testing.T, content []byte, includePkmn bool) *http.Request {
	t.Helper()

	var body bytes.Buffer
	writer := multipart.NewWriter(&body)

	if includePkmn {
		file, err := writer.CreateFormFile(PkmnFormField, "pokemon.pk9")
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

	request := httptest.NewRequest(http.MethodPost, "/api/legality/check", &body)
	request.Header.Set("Content-Type", writer.FormDataContentType())

	return request
}

func TestGetPkmnFromRequestSuccess(t *testing.T) {
	content := []byte("pokemon-bytes")
	request := newMultipartRequest(t, content, true)

	pkmn, err := GetPkmnFromRequest(httptest.NewRecorder(), request)
	if err != nil {
		t.Fatalf("expected no error, got %v", err)
	}

	expected := base64.StdEncoding.EncodeToString(content)
	if pkmn != expected {
		t.Fatalf("expected %q, got %q", expected, pkmn)
	}
}

func TestGetPkmnFromRequestErrors(t *testing.T) {
	tests := []struct {
		name        string
		request     func(t *testing.T) *http.Request
		expectedErr error
	}{
		{
			name: "missing pkmn field",
			request: func(t *testing.T) *http.Request {
				return newMultipartRequest(t, nil, false)
			},
			expectedErr: ErrMissingFile,
		},
		{
			name: "empty pkmn field",
			request: func(t *testing.T) *http.Request {
				return newMultipartRequest(t, []byte{}, true)
			},
			expectedErr: ErrMissingFile,
		},
		{
			name: "body is not multipart",
			request: func(t *testing.T) *http.Request {
				return httptest.NewRequest(http.MethodPost, "/api/legality/check", bytes.NewReader([]byte("not multipart")))
			},
			expectedErr: ErrInvalidForm,
		},
		{
			name: "body is too large",
			request: func(t *testing.T) *http.Request {
				return newMultipartRequest(t, bytes.Repeat([]byte("a"), 2048), true)
			},
			expectedErr: ErrFileTooLarge,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := GetPkmnFromRequest(httptest.NewRecorder(), test.request(t))
			if !errors.Is(err, test.expectedErr) {
				t.Fatalf("expected %v, got %v", test.expectedErr, err)
			}
		})
	}
}

func TestGetPkmnFromRequestWithLimit(t *testing.T) {
	content := bytes.Repeat([]byte("a"), 2048)

	// The legacy limit rejects the body...
	request := newMultipartRequest(t, content, true)
	if _, err := GetPkmnFromRequestWithLimit(httptest.NewRecorder(), request, DefaultPkmnMaxSize); !errors.Is(err, ErrFileTooLarge) {
		t.Fatalf("expected %v, got %v", ErrFileTooLarge, err)
	}

	// ...while the v2 limit accepts it.
	request = newMultipartRequest(t, content, true)
	pkmn, err := GetPkmnFromRequestWithLimit(httptest.NewRecorder(), request, PksmPkmnMaxSize)
	if err != nil {
		t.Fatalf("expected no error, got %v", err)
	}

	expected := base64.StdEncoding.EncodeToString(content)
	if pkmn != expected {
		t.Fatalf("expected %q, got %q", expected, pkmn)
	}
}

func TestRespondPkmnError(t *testing.T) {
	tests := []struct {
		name           string
		err            error
		expectedStatus int
		expectedError  string
	}{
		{
			name:           "file too large",
			err:            ErrFileTooLarge,
			expectedStatus: http.StatusRequestEntityTooLarge,
			expectedError:  "file too large",
		},
		{
			name:           "missing file",
			err:            ErrMissingFile,
			expectedStatus: http.StatusBadRequest,
			expectedError:  "no pokemon file provided",
		},
		{
			name:           "invalid form",
			err:            ErrInvalidForm,
			expectedStatus: http.StatusBadRequest,
			expectedError:  "invalid multipart form",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			recorder := httptest.NewRecorder()
			request := httptest.NewRequest(http.MethodPost, "/api/legality/check", nil)

			if !RespondPkmnError(recorder, request, test.err) {
				t.Fatal("expected the error to be handled")
			}

			if recorder.Code != test.expectedStatus {
				t.Fatalf("expected status %d, got %d", test.expectedStatus, recorder.Code)
			}

			var payload map[string]any
			if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
				t.Fatalf("expected a json body, got %q: %v", recorder.Body.String(), err)
			}

			if payload["error"] != test.expectedError {
				t.Fatalf("expected error %q, got %v", test.expectedError, payload["error"])
			}
		})
	}
}

func TestRespondPkmnErrorUnhandled(t *testing.T) {
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/api/legality/check", nil)

	if RespondPkmnError(recorder, request, errors.New("boom")) {
		t.Fatal("expected the error to be left unhandled")
	}

	if recorder.Code != http.StatusOK {
		t.Fatalf("expected no response, got status %d", recorder.Code)
	}
}

func TestCoreConsoleErrorMessage(t *testing.T) {
	message, ok := CoreConsoleErrorMessage(`{"error": "this pokemon is already legal!"}`)
	if !ok {
		t.Fatal("expected an error message")
	}

	if message != "this pokemon is already legal!" {
		t.Fatalf("unexpected message %q", message)
	}

	if _, ok := CoreConsoleErrorMessage(`{"legal":true,"report":[]}`); ok {
		t.Fatal("expected no error message")
	}

	if _, ok := CoreConsoleErrorMessage("not json"); ok {
		t.Fatal("expected no error message")
	}
}

func TestRespondCoreConsoleErrorInvalidPokemon(t *testing.T) {
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/api/legality/check", nil)

	RespondCoreConsoleError(recorder, request, `{"error": "base64 is not a pokemon"}`, errors.New("CoreConsole exited with code 2"))

	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d", http.StatusBadRequest, recorder.Code)
	}

	body, _ := io.ReadAll(recorder.Body)
	if string(body) != `{"error": "base64 is not a pokemon"}` {
		t.Fatalf("unexpected body %q", string(body))
	}
}
