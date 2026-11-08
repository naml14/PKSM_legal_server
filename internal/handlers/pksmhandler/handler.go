// Package pksmhandler implements the v2 PKSM API routes consumed by the 2ds/3ds
// PKSM application. The contract mirrors FlagBrew/local-gpss: the generation is
// provided through the "generation" header and the game version through the
// "version" header, while the pokemon itself is uploaded as the "pkmn" field
// of a multipart form.
package pksmhandler

import (
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"

	"github.com/FlagBrew/CoreAPI/internal/utils"
	"github.com/apex/log"
	"github.com/getsentry/sentry-go"
	"github.com/go-chi/chi/v5"
	"github.com/lrstanley/chix"
)

// runCoreConsole is the seam used to invoke coreconsole. Tests replace it.
var runCoreConsole = utils.RunCoreConsole

const (
	// GenerationHeader carries the generation of the uploaded pokemon.
	GenerationHeader = "generation"
	// VersionHeader carries the game version used while legalizing.
	VersionHeader = "version"
)

// generationAliases maps every generation understood by PKHeX
// (PKHeX.Core.EntityContext) to its canonical spelling, keyed by the
// lower-cased name so that the client casing does not matter. The legacy
// routes only accept a subset of them, so the legacy enum cannot be reused.
var generationAliases = map[string]string{
	"none":         "None",
	"gen1":         "Gen1",
	"gen2":         "Gen2",
	"gen3":         "Gen3",
	"gen4":         "Gen4",
	"gen5":         "Gen5",
	"gen6":         "Gen6",
	"gen7":         "Gen7",
	"gen8":         "Gen8",
	"gen9":         "Gen9",
	"gen7b":        "Gen7b",
	"gen8a":        "Gen8a",
	"gen8b":        "Gen8b",
	"splitinvalid": "SplitInvalid",
	"maxinvalid":   "MaxInvalid",
}

// legalityResult is the coreconsole payload of a legality check.
type legalityResult struct {
	Legal  bool     `json:"legal"`
	Report []string `json:"report"`
}

// legalizationResult is the coreconsole payload of a legalization attempt.
type legalizationResult struct {
	Legal   bool     `json:"legal"`
	Report  []string `json:"report"`
	Pokemon *string  `json:"pokemon"`
	// Changes lists what the engine had to repair to legalize the pokemon.
	Changes []string `json:"changes"`
}

// legalityResponse is the response returned by the v2 legality route.
type legalityResponse struct {
	Legal  bool     `json:"legal"`
	Report []string `json:"report"`
}

// legalizeResponse is the response returned by the v2 legalize route.
type legalizeResponse struct {
	Legal   bool     `json:"legal"`
	Success bool     `json:"success"`
	Ran     bool     `json:"ran"`
	Report  []string `json:"report"`
	Pokemon *string  `json:"pokemon"`
	// Changes lists what the engine had to repair to legalize the pokemon.
	Changes []string `json:"changes"`
}

type Handler struct {
}

func NewHandler() *Handler {
	return &Handler{}
}

func (h *Handler) Route(r chi.Router) {
	r.Post("/legality", h.legalityCheck)
	r.Post("/legalize", h.legalize)
}

// legalityCheck answers a legality check for the uploaded pokemon.
func (h *Handler) legalityCheck(w http.ResponseWriter, r *http.Request) {
	generation, ok := h.readGeneration(w, r)
	if !ok {
		return
	}

	pkmn, err := utils.GetPkmnFromRequestWithLimit(w, r, utils.PksmPkmnMaxSize)
	if err != nil {
		if utils.RespondPkmnError(w, r, err) {
			return
		}

		chix.Error(w, r, err)
		sentry.CaptureException(err)
		return
	}

	output, err := runCoreConsole(r.Context(), "legality", pkmn, fmt.Sprintf("--generation=%s", generation))
	if err != nil {
		utils.RespondCoreConsoleError(w, r, output, err)
		return
	}

	result := &legalityResult{}
	if err := json.Unmarshal([]byte(output), result); err != nil {
		sentry.CaptureException(err)
		chix.Error(w, r, fmt.Errorf("something went wrong, please try again later"))
		return
	}

	chix.JSON(w, r, http.StatusOK, legalityResponse{
		Legal:  result.Legal,
		Report: result.Report,
	})
}

// legalize answers a legalization request for the uploaded pokemon.
func (h *Handler) legalize(w http.ResponseWriter, r *http.Request) {
	generation, ok := h.readGeneration(w, r)
	if !ok {
		return
	}

	version := r.Header.Get(VersionHeader)
	if version == "" {
		chix.JSON(w, r, http.StatusBadRequest, chix.M{
			"error": "version header is required",
		})
		return
	}

	pkmn, err := utils.GetPkmnFromRequestWithLimit(w, r, utils.PksmPkmnMaxSize)
	if err != nil {
		if utils.RespondPkmnError(w, r, err) {
			return
		}

		chix.Error(w, r, err)
		sentry.CaptureException(err)
		return
	}

	output, err := runCoreConsole(r.Context(), "legalize", pkmn,
		fmt.Sprintf("--generation=%s", generation),
		fmt.Sprintf("--version=%s", version),
	)
	if err != nil {
		utils.RespondCoreConsoleError(w, r, output, err)
		return
	}

	// coreconsole reports some failures (a pokemon that is already legal, for
	// example) with a successful exit code and an error object in the payload.
	// Those are client errors, so they are answered with a 400.
	if _, ok := utils.CoreConsoleErrorMessage(output); ok {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)

		w.Write([]byte(output))
		return
	}

	result := &legalizationResult{}
	if err := json.Unmarshal([]byte(output), result); err != nil {
		sentry.CaptureException(err)
		chix.Error(w, r, fmt.Errorf("something went wrong, please try again later"))
		return
	}

	success := result.Pokemon != nil && *result.Pokemon != ""

	chix.JSON(w, r, http.StatusOK, legalizeResponse{
		Legal:   result.Legal,
		Success: success,
		Ran:     true,
		Report:  result.Report,
		Pokemon: result.Pokemon,
		Changes: result.Changes,
	})
}

// readGeneration reads and validates the mandatory generation header.
func (h *Handler) readGeneration(w http.ResponseWriter, r *http.Request) (string, bool) {
	raw := r.Header.Get(GenerationHeader)
	if raw == "" {
		// Some clients pass the generation as a query parameter instead.
		raw = r.URL.Query().Get(GenerationHeader)
	}

	if strings.TrimSpace(raw) == "" {
		chix.JSON(w, r, http.StatusBadRequest, chix.M{
			"error": "version header is required",
		})
		return "", false
	}

	generation, ok := normalizeGeneration(raw)
	if !ok {
		log.WithField("value", raw).Warn("pksm: unsupported generation")
		chix.JSON(w, r, http.StatusBadRequest, chix.M{
			"error": "invalid generation",
		})
		return "", false
	}

	log.WithField("value", raw).WithField("generation", generation).Info("pksm: generation")

	return generation, true
}

// normalizeGeneration maps the generation sent by the client to the canonical
// name understood by coreconsole. Casing and surrounding spaces are ignored
// and a bare generation number ("9") is accepted as well.
func normalizeGeneration(raw string) (string, bool) {
	trimmed := strings.TrimSpace(raw)

	if canonical, ok := generationAliases[strings.ToLower(trimmed)]; ok {
		return canonical, true
	}

	if number, err := strconv.Atoi(trimmed); err == nil && number >= 1 && number <= 9 {
		return fmt.Sprintf("Gen%d", number), true
	}

	return "", false
}
