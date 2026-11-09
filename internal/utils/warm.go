package utils

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"strings"
	"sync"
	"syscall"
)

// warmEnginePoolSize is the maximum number of warm engine processes kept
// alive. The VPS is small, so the pool is capped.
const warmEnginePoolSize = 4

// maxServeResponseSize is the maximum size of a single response line read
// from a warm engine. The response carries the pokemon in base64, so a few
// hundred bytes are expected, but the limit keeps a broken engine from
// exhausting the memory of the API.
const maxServeResponseSize = 4 << 20

// engineNewline is the line ending the engine writes its output with. It is
// the one the .NET runtime uses, so the output of the warm engine is the same
// as the output of the one-shot engine.
var engineNewline = func() string {
	if runtime.GOOS == "windows" {
		return "\r\n"
	}

	return "\n"
}()

// ErrWarmEngineUnavailable signals that the warm engine could not serve the
// request (it could not be started, it died or it broke the protocol). The
// caller falls back to the one-shot engine.
var ErrWarmEngineUnavailable = errors.New("warm engine unavailable")

// ErrWarmEngineCanceled signals that the context of the request was canceled
// or that it timed out. The warm engine is killed and the error is returned
// as-is: falling back to the one-shot engine would only double the wait.
var ErrWarmEngineCanceled = errors.New("warm engine request canceled")

// serveRequest is the JSON line sent to a warm engine process.
type serveRequest struct {
	// Command is the CLI command to run ("legality", "legalize" or "summary").
	Command string `json:"command"`
	// Pokemon is the base64 of the pokemon.
	Pokemon string `json:"pokemon"`
	// Generation is the name of the EntityContext of the pokemon, if any.
	Generation *string `json:"generation"`
	// LegalizationGeneration is the name of the EntityContext the pokemon has
	// to be legalized into, if any.
	LegalizationGeneration *string `json:"legalizationGeneration"`
	// Version is the name of the GameVersion used while legalizing, if any.
	Version *string `json:"version"`
	// Args carries the flags that are not part of the protocol.
	Args []string `json:"args,omitempty"`
}

// serveResponse is the JSON line returned by a warm engine process.
type serveResponse struct {
	// ExitCode is the exit code the one-shot CLI would have returned.
	ExitCode int `json:"exitCode"`
	// Output is the text the one-shot CLI would have printed.
	Output string `json:"output"`
	// ErrorOutput is the text the one-shot CLI would have printed on the
	// standard error. The engine may leave it empty; the API then falls back
	// to the one-shot engine to get the exact same text.
	ErrorOutput string `json:"errorOutput,omitempty"`
}

// warmModes are the commands the serve protocol of the engine understands.
// Every other command ("version", ...) keeps using the one-shot engine.
var warmModes = map[string]bool{
	"legality": true,
	"legalize": true,
	"summary":  true,
}

// coreConsoleLauncher builds the process that runs coreconsole. Tests
// replace it with a fake process.
var coreConsoleLauncher = defaultCoreConsoleLauncher

// defaultCoreConsoleLauncher builds the coreconsole process of the repository.
func defaultCoreConsoleLauncher(args []string) (*exec.Cmd, error) {
	cmd := exec.Command("./coreconsole", args...)
	cmd.Dir = "./cc"

	return cmd, nil
}

// warmEngines is the pool of warm engines used by RunCoreConsole.
var warmEngines = newEnginePool(warmEnginePoolSize)

// init closes the warm engines when the API is asked to stop, so that no
// engine process outlives the API.
func init() {
	go func() {
		signals := make(chan os.Signal, 1)
		signal.Notify(signals, os.Interrupt, syscall.SIGTERM)
		<-signals

		ShutdownWarmEngines()
	}()
}

// ShutdownWarmEngines stops every warm engine process of the pool.
func ShutdownWarmEngines() {
	warmEngines.close()
}

// buildServeRequest turns a coreconsole invocation into a serve request. The
// second return value reports whether the warm engine can serve the command
// at all.
func buildServeRequest(mode, pokemon string, extraArgs []string) (serveRequest, bool) {
	if !warmModes[mode] || pokemon == "" {
		return serveRequest{}, false
	}

	request := serveRequest{
		Command: mode,
		Pokemon: pokemon,
	}

	for _, arg := range extraArgs {
		switch {
		case strings.HasPrefix(arg, "--generation="):
			if value := strings.TrimPrefix(arg, "--generation="); value != "" {
				request.Generation = &value
			}
		case strings.HasPrefix(arg, "--legalization-generation="):
			if value := strings.TrimPrefix(arg, "--legalization-generation="); value != "" {
				request.LegalizationGeneration = &value
			}
		case strings.HasPrefix(arg, "--version="):
			if value := strings.TrimPrefix(arg, "--version="); value != "" {
				request.Version = &value
			}
		default:
			// Any other flag is handed over to the engine as it is.
			request.Args = append(request.Args, arg)
		}
	}

	return request, true
}

// warmEngine is a coreconsole process running in serve mode.
type warmEngine struct {
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	stdout *bufio.Reader
	// stdoutPipe is the read end of the standard output of the engine.
	stdoutPipe *os.File
	stderr     *boundedBuffer

	// requestMu serializes the requests of the engine: a request is only
	// answered by the engine that received it.
	requestMu sync.Mutex
	// stateMu guards the dead flag.
	stateMu sync.Mutex
	dead    bool
}

// startWarmEngine starts a coreconsole process in serve mode.
func startWarmEngine() (*warmEngine, error) {
	cmd, err := coreConsoleLauncher([]string{"serve"})
	if err != nil {
		return nil, err
	}

	stdinRead, stdinWrite, err := os.Pipe()
	if err != nil {
		return nil, err
	}

	stdoutRead, stdoutWrite, err := os.Pipe()
	if err != nil {
		_ = stdinRead.Close()
		_ = stdinWrite.Close()

		return nil, err
	}

	stderr := &boundedBuffer{max: 64 * 1024}
	cmd.Stdin = stdinRead
	cmd.Stdout = stdoutWrite
	cmd.Stderr = stderr

	if err := cmd.Start(); err != nil {
		_ = stdinRead.Close()
		_ = stdinWrite.Close()
		_ = stdoutRead.Close()
		_ = stdoutWrite.Close()

		return nil, err
	}

	// The parent must not hold the ends of the pipes it does not use.
	_ = stdinRead.Close()
	_ = stdoutWrite.Close()

	engine := &warmEngine{
		cmd:        cmd,
		stdin:      stdinWrite,
		stdout:     bufio.NewReaderSize(stdoutRead, 64*1024),
		stdoutPipe: stdoutRead,
		stderr:     stderr,
	}

	// The engine is reaped in the background so that it does not become a
	// zombie and so that its output pipe is released when it dies.
	go engine.reap()

	return engine, nil
}

// reap waits for the engine process and marks the engine as dead.
func (e *warmEngine) reap() {
	_ = e.cmd.Wait()
	e.markDead()
	_ = e.stdoutPipe.Close()
}

// markDead flags the engine as unusable.
func (e *warmEngine) markDead() {
	e.stateMu.Lock()
	e.dead = true
	e.stateMu.Unlock()
}

// isDead reports whether the engine is unusable.
func (e *warmEngine) isDead() bool {
	e.stateMu.Lock()
	defer e.stateMu.Unlock()

	return e.dead
}

// terminate kills the engine process and releases its pipes.
func (e *warmEngine) terminate() {
	e.markDead()

	if e.cmd.Process != nil {
		_ = e.cmd.Process.Kill()
	}

	_ = e.stdin.Close()
}

// do sends a request to the engine and waits for its answer. The engine is
// killed when the context is canceled or times out.
func (e *warmEngine) do(ctx context.Context, request serveRequest) (serveResponse, error) {
	e.requestMu.Lock()
	defer e.requestMu.Unlock()

	if err := ctx.Err(); err != nil {
		return serveResponse{}, fmt.Errorf("%w: %v", ErrWarmEngineCanceled, err)
	}

	if e.isDead() {
		return serveResponse{}, fmt.Errorf("%w: the engine is not running", ErrWarmEngineUnavailable)
	}

	payload, err := json.Marshal(request)
	if err != nil {
		return serveResponse{}, err
	}

	payload = append(payload, '\n')

	type outcome struct {
		response serveResponse
		err      error
	}

	done := make(chan outcome, 1)

	go func() {
		if _, err := e.stdin.Write(payload); err != nil {
			done <- outcome{err: fmt.Errorf("%w: %v", ErrWarmEngineUnavailable, err)}

			return
		}

		line, err := readLimitedLine(e.stdout, maxServeResponseSize)
		if err != nil {
			done <- outcome{err: fmt.Errorf("%w: %v", ErrWarmEngineUnavailable, err)}

			return
		}

		var response serveResponse
		if err := json.Unmarshal(line, &response); err != nil {
			done <- outcome{err: fmt.Errorf("%w: %v", ErrWarmEngineUnavailable, err)}

			return
		}

		done <- outcome{response: response}
	}()

	select {
	case <-ctx.Done():
		// The engine would keep the process alive for nothing, so it is
		// killed. The one-shot engine is not used as a fallback here: that
		// would only double the time the client waits.
		e.terminate()

		return serveResponse{}, fmt.Errorf("%w: %v", ErrWarmEngineCanceled, ctx.Err())
	case result := <-done:
		if result.err != nil {
			// The engine broke the protocol or died: it is discarded.
			e.markDead()

			return serveResponse{}, result.err
		}

		return result.response, nil
	}
}

// readLimitedLine reads a single line, refusing lines longer than max.
func readLimitedLine(reader *bufio.Reader, max int) ([]byte, error) {
	var line bytes.Buffer

	for {
		chunk, err := reader.ReadSlice('\n')
		if err == bufio.ErrBufferFull {
			if line.Len()+len(chunk) > max {
				return nil, fmt.Errorf("the response line is longer than %d bytes", max)
			}

			line.Write(chunk)

			continue
		}

		if err != nil {
			return nil, err
		}

		if line.Len()+len(chunk) > max {
			return nil, fmt.Errorf("the response line is longer than %d bytes", max)
		}

		line.Write(chunk)

		return bytes.TrimRight(line.Bytes(), "\r\n"), nil
	}
}

// boundedBuffer keeps the last bytes written to it, so the diagnostics of the
// engine cannot exhaust the memory of the API.
type boundedBuffer struct {
	mu  sync.Mutex
	buf []byte
	max int
}

// Write keeps the last bytes of the output of the engine.
func (b *boundedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	b.buf = append(b.buf, p...)
	if len(b.buf) > b.max {
		b.buf = b.buf[len(b.buf)-b.max:]
	}

	return len(p), nil
}

// String returns the text the engine wrote.
func (b *boundedBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()

	return string(b.buf)
}

// enginePool keeps a bounded number of warm engines alive.
type enginePool struct {
	// max is the maximum number of engines of the pool.
	max int
	// start builds a new engine.
	start func() (*warmEngine, error)

	// tokens holds the free slots of the pool. A slot is taken when an
	// engine is created and returned when the engine is discarded.
	tokens chan struct{}
	// idle holds the engines that are ready to serve a request.
	idle chan *warmEngine

	mu     sync.Mutex
	closed bool
}

// newEnginePool builds a pool of at most max engines.
func newEnginePool(max int) *enginePool {
	// The slots of the pool are free slots, so they are available right
	// away: a slot is taken when an engine is created.
	tokens := make(chan struct{}, max)
	for i := 0; i < max; i++ {
		tokens <- struct{}{}
	}

	return &enginePool{
		max:    max,
		start:  startWarmEngine,
		tokens: tokens,
		idle:   make(chan *warmEngine, max),
	}
}

// acquire returns an engine ready to serve a request. An idle engine is
// reused; a new one is started while the pool has room; otherwise the caller
// waits for an engine to be released.
func (p *enginePool) acquire(ctx context.Context) (*warmEngine, error) {
	if err := ctx.Err(); err != nil {
		return nil, fmt.Errorf("%w: %v", ErrWarmEngineCanceled, err)
	}

	p.mu.Lock()
	closed := p.closed
	p.mu.Unlock()

	if closed {
		return nil, fmt.Errorf("%w: the pool is closed", ErrWarmEngineUnavailable)
	}

	// An engine that is already running is reused.
	select {
	case engine := <-p.idle:
		return engine, nil
	default:
	}

	// A new engine is started while the pool has room for it.
	select {
	case <-p.tokens:
		engine, err := p.start()
		if err != nil {
			p.tokens <- struct{}{}

			return nil, fmt.Errorf("%w: %v", ErrWarmEngineUnavailable, err)
		}

		return engine, nil
	default:
	}

	select {
	case engine := <-p.idle:
		return engine, nil
	case <-ctx.Done():
		return nil, fmt.Errorf("%w: %v", ErrWarmEngineCanceled, ctx.Err())
	}
}

// release gives an engine back to the pool. A dead engine is discarded.
func (p *enginePool) release(engine *warmEngine) {
	if engine == nil {
		return
	}

	p.mu.Lock()
	closed := p.closed
	p.mu.Unlock()

	if closed || engine.isDead() {
		engine.terminate()
		p.tokens <- struct{}{}

		return
	}

	select {
	case p.idle <- engine:
	default:
		engine.terminate()
		p.tokens <- struct{}{}
	}
}

// run serves a request with a warm engine. A broken engine is discarded and
// the request is retried once with another engine; when that fails too the
// error is returned and the caller falls back to the one-shot engine.
func (p *enginePool) run(ctx context.Context, request serveRequest) (serveResponse, error) {
	var lastErr error

	for attempt := 0; attempt < 2; attempt++ {
		engine, err := p.acquire(ctx)
		if err != nil {
			if errors.Is(err, ErrWarmEngineCanceled) {
				return serveResponse{}, err
			}

			lastErr = err

			continue
		}

		response, err := engine.do(ctx, request)
		p.release(engine)

		if err == nil {
			return response, nil
		}

		if errors.Is(err, ErrWarmEngineCanceled) {
			return serveResponse{}, err
		}

		lastErr = err
	}

	return serveResponse{}, lastErr
}

// close stops every engine of the pool.
func (p *enginePool) close() {
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()

		return
	}

	p.closed = true
	p.mu.Unlock()

	for {
		select {
		case engine := <-p.idle:
			engine.terminate()
			p.tokens <- struct{}{}
		default:
			return
		}
	}
}
