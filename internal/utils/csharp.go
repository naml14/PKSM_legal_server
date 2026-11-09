package utils

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os/exec"
	"strings"
	"syscall"
)

// RunCoreConsole runs the coreconsole engine and returns its standard output.
//
// The warm engine is used first: the process stays alive between requests, so
// the API does not pay the cold start of the engine on every call. When the
// warm engine is not available (it could not be started, it died or it broke
// the protocol) the one-shot engine takes over, so the behaviour of the API
// is the same as it has always been.
func RunCoreConsole(ctx context.Context, mode, pokemon string, extraArgs ...string) (string, error) {
	if request, ok := buildServeRequest(mode, pokemon, extraArgs); ok {
		response, err := warmEngines.run(ctx, request)
		switch {
		case err == nil:
			return warmResponse(ctx, mode, pokemon, extraArgs, response)
		case errors.Is(err, ErrWarmEngineCanceled):
			// The request was canceled or it timed out: the engine was
			// killed and the one-shot engine is not used, because that
			// would only double the time the client waits.
			return "", err
		}

		// The warm engine is not available: the one-shot engine takes over.
		return runCoreConsoleOnce(ctx, mode, pokemon, extraArgs...)
	}

	return runCoreConsoleOnce(ctx, mode, pokemon, extraArgs...)
}

// warmResponse turns the answer of a warm engine into the output and the
// error the caller expects, exactly as the one-shot engine would have
// returned them.
func warmResponse(ctx context.Context, mode, pokemon string, extraArgs []string, response serveResponse) (string, error) {
	if response.ExitCode == 0 {
		return response.Output, nil
	}

	// The engine reports the error text of the failed command on the
	// standard error. When it does not, the one-shot engine is used to get
	// the exact same text as before.
	if response.ErrorOutput == "" && response.Output == "" {
		return runCoreConsoleOnce(ctx, mode, pokemon, extraArgs...)
	}

	output := response.ErrorOutput
	if output == "" {
		output = response.Output
	}

	// The CLI writes the reason of a failure with a line ending and the
	// serve mode of the engine does not include it in the answer, so it is
	// added back: the output is then exactly the one of the one-shot engine.
	if !strings.HasSuffix(output, "\n") {
		output += engineNewline
	}

	return output, fmt.Errorf("CoreConsole exited with code %d", response.ExitCode)
}

// runCoreConsoleOnce runs the coreconsole engine for a single request.
func runCoreConsoleOnce(ctx context.Context, mode, pokemon string, extraArgs ...string) (string, error) {
	args := []string{mode}
	if pokemon != "" {
		args = append(args, pokemon)
	}
	args = append(args, extraArgs...)

	cmd, err := coreConsoleLauncher(args)
	if err != nil {
		return "", err
	}

	var out bytes.Buffer
	var errBytes bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errBytes

	ch := make(chan error)
	go func() {
		ch <- cmd.Run()
	}()
	errored := false
	killed := false
	exitCode := 0
	select {
	case <-ctx.Done():
		if err := cmd.Process.Signal(syscall.SIGINT); err != nil {
			return "", err
		}
		killed = true
	case err := <-ch:
		if err != nil {
			if exitError, ok := err.(*exec.ExitError); ok {
				exitCode = exitError.ExitCode()
			}
			errored = true
		}
	}

	if killed {
		return "", fmt.Errorf("process killed due request cancellation")
	}

	if errored {
		return errBytes.String(), fmt.Errorf("CoreConsole exited with code %d", exitCode)
	}

	return out.String(), nil
}
