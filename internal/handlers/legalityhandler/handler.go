package legalityhandler

import (
	"fmt"
	"net/http"

	"github.com/FlagBrew/CoreAPI/internal/models"
	"github.com/FlagBrew/CoreAPI/internal/utils"
	"github.com/getsentry/sentry-go"
	"github.com/go-chi/chi/v5"
	"github.com/lrstanley/chix"
)

// runCoreConsole is the seam used to invoke coreconsole. Tests replace it.
var runCoreConsole = utils.RunCoreConsole

type Handler struct {
}

func NewHandler() *Handler {
	return &Handler{}
}

func (h *Handler) Route(r chi.Router) {
	r.Post("/check", h.legalityReport)
	r.Post("/legalize", h.autoLegality)
}

func (h *Handler) legalityReport(w http.ResponseWriter, r *http.Request) {
	// Get the pkmn in base64. This happens before the request is bound so that
	// malformed uploads are reported with a clean error instead of a 500.
	pkmn, err := utils.GetPkmnFromRequest(w, r)
	if err != nil {
		if utils.RespondPkmnError(w, r, err) {
			return
		}

		chix.Error(w, r, err)
		sentry.CaptureException(err)
		return
	}

	legalityReportRequest := &models.LegalityCheckRequest{}

	if chix.Error(w, r, chix.Bind(r, legalityReportRequest)) {
		return
	}

	// Run the coreconsole script
	var extraArgs []string
	if legalityReportRequest.Generation != "" {
		extraArgs = append(extraArgs, fmt.Sprintf("--generation=%s", legalityReportRequest.Generation))
	}

	output, err := runCoreConsole(r.Context(), "legality", pkmn, extraArgs...)
	if err != nil {
		utils.RespondCoreConsoleError(w, r, output, err)
		return
	}

	// we got JSON back, so we can just write it to the response
	w.Header().Set("Content-Type", "application/json")
	w.Write([]byte(output))
}

func (h *Handler) autoLegality(w http.ResponseWriter, r *http.Request) {
	// Get the pkmn in base64. This happens before the request is bound so that
	// malformed uploads are reported with a clean error instead of a 500.
	pkmn, err := utils.GetPkmnFromRequest(w, r)
	if err != nil {
		if utils.RespondPkmnError(w, r, err) {
			return
		}

		chix.Error(w, r, err)
		sentry.CaptureException(err)
		return
	}

	autoLegalityRequest := &models.LegalizeRequest{}

	if chix.Error(w, r, chix.Bind(r, autoLegalityRequest)) {
		return
	}

	// Run the coreconsole script
	var extraArgs []string
	if autoLegalityRequest.Generation != "" {
		extraArgs = append(extraArgs, fmt.Sprintf("--generation=%s", autoLegalityRequest.Generation))
	}

	if autoLegalityRequest.ForcedGeneration != "" {
		extraArgs = append(extraArgs, fmt.Sprintf("--legalization-generation=%s", autoLegalityRequest.ForcedGeneration))
	}

	if autoLegalityRequest.ForcedVersion != "" {
		extraArgs = append(extraArgs, fmt.Sprintf("--version=%s", autoLegalityRequest.ForcedVersion))
	}

	output, err := runCoreConsole(r.Context(), "legalize", pkmn, extraArgs...)
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

	// we got JSON back, so we can just write it to the response
	w.Header().Set("Content-Type", "application/json")
	w.Write([]byte(output))
}
