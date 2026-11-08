package infohandler

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
	r.Post("/", h.getInfo)
}

func (h *Handler) getInfo(w http.ResponseWriter, r *http.Request) {
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

	infoRequest := &models.GetInfoRequest{}

	if chix.Error(w, r, chix.Bind(r, infoRequest)) {
		return
	}

	// Run the coreconsole script
	var extraArgs []string
	if infoRequest.Generation != "" {
		extraArgs = append(extraArgs, fmt.Sprintf("--generation=%s", infoRequest.Generation))
	}

	output, err := runCoreConsole(r.Context(), "summary", pkmn, extraArgs...)
	if err != nil {
		utils.RespondCoreConsoleError(w, r, output, err)
		return
	}

	// we got JSON back, so we can just write it to the response
	w.Header().Set("Content-Type", "application/json")
	w.Write([]byte(output))
}
