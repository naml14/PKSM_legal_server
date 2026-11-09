package utils

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

// The tests of the warm engine use the test binary itself as a fake
// coreconsole: the binary looks at the environment and runs the fake engine
// instead of the tests when it is started as a child process.
const (
	fakeModeEnv     = "PKSM_FAKE_CORE_MODE"
	fakePidFileEnv  = "PKSM_FAKE_CORE_PID_FILE"
	fakeExitCodeEnv = "PKSM_FAKE_CORE_EXIT_CODE"
	fakeStderrEnv   = "PKSM_FAKE_CORE_STDERR"
)

// fakePayload is the answer of the fake engine. It carries every value the
// request carried, so the warm path and the one-shot path can be compared.
type fakePayload struct {
	Command                string   `json:"command"`
	Pokemon                string   `json:"pokemon"`
	Generation             *string  `json:"generation"`
	LegalizationGeneration *string  `json:"legalizationGeneration"`
	Version                *string  `json:"version"`
	Args                   []string `json:"args,omitempty"`
}

// TestMain runs the fake engine when the test binary is started as a child
// process of the tests.
func TestMain(m *testing.M) {
	switch os.Getenv(fakeModeEnv) {
	case "serve":
		os.Exit(fakeServe())
	case "serve_minimal":
		os.Exit(fakeServeMinimal())
	case "serve_fail":
		// The engine dies right away: it cannot serve any request.
		os.Exit(3)
	case "oneshot":
		os.Exit(fakeOneShot())
	}

	os.Exit(m.Run())
}

// fakeOneShot mimics the one-shot CLI: it prints the payload of the request
// built from the command line arguments.
func fakeOneShot() int {
	payload := fakePayloadFromArgs(os.Args[1:])

	if code := fakeExitCode(); code != 0 {
		fmt.Fprint(os.Stderr, fakeStderr()+fakeNewline())

		return code
	}

	fmt.Fprint(os.Stdout, fakeRender(payload))

	return 0
}

// fakeServe mimics the serve mode of the engine: it answers every request
// line with a response line that carries the output and the exit code of the
// command, plus the text the command would have printed on the standard
// error.
func fakeServe() int {
	fakeRegisterPID()

	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 0, 64*1024), maxServeResponseSize)

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}

		var request serveRequest
		if err := json.Unmarshal([]byte(line), &request); err != nil {
			continue
		}

		// The CLI prints the payload of a successful command on the standard
		// output and the reason of a failure on the standard error.
		exitCode := fakeExitCode()
		stdout := ""
		if exitCode == 0 {
			stdout = fakeRender(fakePayloadFromRequest(request))
		}

		response := serveResponse{
			ExitCode:    exitCode,
			Output:      stdout,
			ErrorOutput: fakeStderr(),
		}

		answer, err := json.Marshal(response)
		if err != nil {
			return 1
		}

		fmt.Fprintln(os.Stdout, string(answer))
	}

	return 0
}

// fakeServeMinimal mimics an engine that only reports the standard output of
// the command, without the text of the standard error.
func fakeServeMinimal() int {
	fakeRegisterPID()

	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 0, 64*1024), maxServeResponseSize)

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}

		var request serveRequest
		if err := json.Unmarshal([]byte(line), &request); err != nil {
			continue
		}

		exitCode := fakeExitCode()
		stdout := ""
		if exitCode == 0 {
			stdout = fakeRender(fakePayloadFromRequest(request))
		}

		response := serveResponse{
			ExitCode: exitCode,
			Output:   stdout,
		}

		answer, err := json.Marshal(response)
		if err != nil {
			return 1
		}

		fmt.Fprintln(os.Stdout, string(answer))
	}

	return 0
}

// fakeRegisterPID writes the pid of the fake engine to the pid file, so that
// the tests can tell whether an engine was reused or a new one was started.
func fakeRegisterPID() {
	path := os.Getenv(fakePidFileEnv)
	if path == "" {
		return
	}

	file, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return
	}

	defer file.Close()

	fmt.Fprintf(file, "%d\n", os.Getpid())
}

// fakeExitCode returns the exit code the fake engine has to report.
func fakeExitCode() int {
	var code int
	if _, err := fmt.Sscanf(os.Getenv(fakeExitCodeEnv), "%d", &code); err != nil {
		return 0
	}

	return code
}

// fakeStderr returns the text the fake engine has to print on the standard
// error.
func fakeStderr() string {
	return os.Getenv(fakeStderrEnv)
}

// fakeNewline is the line ending the CLI writes its output with.
func fakeNewline() string {
	if runtime.GOOS == "windows" {
		return "\r\n"
	}

	return "\n"
}

// fakeRender prints the payload the way the CLI prints it.
func fakeRender(payload fakePayload) string {
	encoded, err := json.Marshal(payload)
	if err != nil {
		return ""
	}

	return string(encoded) + "\n"
}

// fakePayloadFromArgs builds the payload of a request built from the command
// line arguments of the one-shot CLI.
func fakePayloadFromArgs(args []string) fakePayload {
	payload := fakePayload{}
	if len(args) == 0 {
		return payload
	}

	payload.Command = args[0]

	rest := args[1:]
	if len(rest) > 0 && !strings.HasPrefix(rest[0], "--") {
		payload.Pokemon = rest[0]
		rest = rest[1:]
	}

	for _, arg := range rest {
		switch {
		case strings.HasPrefix(arg, "--generation="):
			value := strings.TrimPrefix(arg, "--generation=")
			payload.Generation = &value
		case strings.HasPrefix(arg, "--legalization-generation="):
			value := strings.TrimPrefix(arg, "--legalization-generation=")
			payload.LegalizationGeneration = &value
		case strings.HasPrefix(arg, "--version="):
			value := strings.TrimPrefix(arg, "--version=")
			payload.Version = &value
		default:
			payload.Args = append(payload.Args, arg)
		}
	}

	return payload
}

// fakePayloadFromRequest builds the payload of a request received by the
// serve mode of the engine.
func fakePayloadFromRequest(request serveRequest) fakePayload {
	return fakePayload{
		Command:                request.Command,
		Pokemon:                request.Pokemon,
		Generation:             request.Generation,
		LegalizationGeneration: request.LegalizationGeneration,
		Version:                request.Version,
		Args:                   request.Args,
	}
}

// fakeEngine is the state of the fake engine used by a test.
type fakeEngine struct {
	mu sync.Mutex
	// calls records the arguments of every invocation of the engine.
	calls [][]string
	// serveMode is the mode the fake engine runs in when it is started in
	// serve mode.
	serveMode string
	// exitCode is the exit code the fake engine reports.
	exitCode int
	// stderr is the text the fake engine prints on the standard error.
	stderr string
	// pidFile records the pid of every engine started in serve mode.
	pidFile string
	// workDir is the working directory of the fake engine.
	workDir string
}

// setupFakeEngine replaces the launcher of the engine with the fake engine
// and gives the test a clean pool.
func setupFakeEngine(t *testing.T) *fakeEngine {
	t.Helper()

	engine := &fakeEngine{
		serveMode: "serve",
		pidFile:   filepath.Join(t.TempDir(), "pids.txt"),
		workDir:   t.TempDir(),
	}

	originalLauncher := coreConsoleLauncher
	originalPool := warmEngines

	coreConsoleLauncher = engine.launcher
	warmEngines = newEnginePool(warmEnginePoolSize)

	t.Cleanup(func() {
		warmEngines.close()
		warmEngines = originalPool
		coreConsoleLauncher = originalLauncher
	})

	return engine
}

// launcher builds the command of the fake engine.
func (e *fakeEngine) launcher(args []string) (*exec.Cmd, error) {
	e.mu.Lock()
	e.calls = append(e.calls, append([]string{}, args...))
	mode := "oneshot"
	if len(args) > 0 && args[0] == "serve" {
		mode = e.serveMode
	}
	exitCode := e.exitCode
	stderr := e.stderr
	e.mu.Unlock()

	if mode == "serve_missing" {
		// The engine does not exist, so it cannot be started.
		return exec.Command(filepath.Join(e.workDir, "coreconsole-does-not-exist")), nil
	}

	executable, err := os.Executable()
	if err != nil {
		return nil, err
	}

	cmd := exec.Command(executable, args...)
	cmd.Env = append(os.Environ(),
		fakeModeEnv+"="+mode,
		fakePidFileEnv+"="+e.pidFile,
		fmt.Sprintf("%s=%d", fakeExitCodeEnv, exitCode),
		fakeStderrEnv+"="+stderr,
	)

	return cmd, nil
}

// callsFor returns the invocations of the engine whose first argument is the
// given command.
func (e *fakeEngine) callsFor(command string) [][]string {
	e.mu.Lock()
	defer e.mu.Unlock()

	var calls [][]string
	for _, call := range e.calls {
		if len(call) > 0 && call[0] == command {
			calls = append(calls, call)
		}
	}

	return calls
}

// startedEngines returns the pids of the engines started in serve mode.
func (e *fakeEngine) startedEngines(t *testing.T) []string {
	t.Helper()

	content, err := os.ReadFile(e.pidFile)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}

		t.Fatalf("failed to read the pid file: %v", err)
	}

	var pids []string
	for _, line := range strings.Split(strings.TrimSpace(string(content)), "\n") {
		if line = strings.TrimSpace(line); line != "" {
			pids = append(pids, line)
		}
	}

	return pids
}

// TestWarmEngineMatchesTheOneShotEngine checks that the warm engine returns
// the same output and the same exit code as the one-shot engine.
func TestWarmEngineMatchesTheOneShotEngine(t *testing.T) {
	engine := setupFakeEngine(t)

	warm, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7", "--version=ORAS")
	if err != nil {
		t.Fatalf("the warm engine failed: %v", err)
	}

	oneShot, err := runCoreConsoleOnce(context.Background(), "legality", "PKMN", "--generation=Gen7", "--version=ORAS")
	if err != nil {
		t.Fatalf("the one-shot engine failed: %v", err)
	}

	if warm != oneShot {
		t.Fatalf("the warm engine and the one-shot engine disagree:\n warm: %q\n one shot: %q", warm, oneShot)
	}

	for _, expected := range []string{`"command":"legality"`, `"pokemon":"PKMN"`, `"generation":"Gen7"`, `"version":"ORAS"`} {
		if !strings.Contains(warm, expected) {
			t.Fatalf("the output of the engine does not carry %s: %q", expected, warm)
		}
	}

	if len(engine.callsFor("serve")) == 0 {
		t.Fatal("the warm engine was not used")
	}
}

// TestWarmEngineMatchesTheOneShotEngineForEveryCommand checks the same for
// every command the serve protocol understands.
func TestWarmEngineMatchesTheOneShotEngineForEveryCommand(t *testing.T) {
	setupFakeEngine(t)

	cases := []struct {
		mode      string
		pokemon   string
		extraArgs []string
	}{
		{mode: "legality", pokemon: "PKMN", extraArgs: []string{"--generation=Gen9"}},
		{mode: "legalize", pokemon: "PKMN", extraArgs: []string{"--generation=Gen9", "--version=SV"}},
		{mode: "legalize", pokemon: "PKMN", extraArgs: []string{"--generation=Gen9", "--legalization-generation=Gen7", "--version=ORAS"}},
		{mode: "summary", pokemon: "PKMN", extraArgs: []string{"--generation=Gen7"}},
		{mode: "legality", pokemon: "PKMN"},
	}

	for _, testCase := range cases {
		warm, err := RunCoreConsole(context.Background(), testCase.mode, testCase.pokemon, testCase.extraArgs...)
		if err != nil {
			t.Fatalf("the warm engine failed for %s: %v", testCase.mode, err)
		}

		oneShot, err := runCoreConsoleOnce(context.Background(), testCase.mode, testCase.pokemon, testCase.extraArgs...)
		if err != nil {
			t.Fatalf("the one-shot engine failed for %s: %v", testCase.mode, err)
		}

		if warm != oneShot {
			t.Fatalf("the warm engine and the one-shot engine disagree for %s:\n warm: %q\n one shot: %q", testCase.mode, warm, oneShot)
		}
	}
}

// TestWarmEngineReusesTheSameProcess checks that two consecutive requests
// are served by the same engine process.
func TestWarmEngineReusesTheSameProcess(t *testing.T) {
	engine := setupFakeEngine(t)

	first, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if err != nil {
		t.Fatalf("the first request failed: %v", err)
	}

	second, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if err != nil {
		t.Fatalf("the second request failed: %v", err)
	}

	if first != second {
		t.Fatalf("the two requests returned different outputs:\n first: %q\n second: %q", first, second)
	}

	engines := engine.startedEngines(t)
	if len(engines) != 1 {
		t.Fatalf("expected a single engine to be started, got %d: %v", len(engines), engines)
	}
}

// TestWarmEngineFallsBackToTheOneShotEngine checks that a warm engine that
// cannot serve the request does not break the API: the one-shot engine
// answers instead.
func TestWarmEngineFallsBackToTheOneShotEngine(t *testing.T) {
	cases := []struct {
		name      string
		serveMode string
	}{
		{name: "the engine dies", serveMode: "serve_fail"},
		{name: "the engine cannot be started", serveMode: "serve_missing"},
	}

	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			engine := setupFakeEngine(t)
			engine.serveMode = testCase.serveMode

			output, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7")
			if err != nil {
				t.Fatalf("the request failed instead of falling back: %v", err)
			}

			expected, err := runCoreConsoleOnce(context.Background(), "legality", "PKMN", "--generation=Gen7")
			if err != nil {
				t.Fatalf("the one-shot engine failed: %v", err)
			}

			if output != expected {
				t.Fatalf("the fallback did not return the output of the one-shot engine:\n got: %q\n want: %q", output, expected)
			}

			if len(engine.callsFor("legality")) == 0 {
				t.Fatal("the one-shot engine was not used")
			}
		})
	}
}

// TestWarmEngineCancellation checks that a canceled request returns an error
// right away and does not fall back to the one-shot engine.
func TestWarmEngineCancellation(t *testing.T) {
	engine := setupFakeEngine(t)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	start := time.Now()
	output, err := RunCoreConsole(ctx, "legality", "PKMN", "--generation=Gen7")
	if err == nil {
		t.Fatalf("the canceled request returned no error: %q", output)
	}

	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("the canceled request took %s", elapsed)
	}

	if len(engine.callsFor("legality")) != 0 {
		t.Fatal("the canceled request fell back to the one-shot engine")
	}
}

// TestWarmEngineReportsTheExitCodeOfTheCommand checks that a command that
// fails is reported with the exit code and the output of the one-shot engine.
func TestWarmEngineReportsTheExitCodeOfTheCommand(t *testing.T) {
	engine := setupFakeEngine(t)
	engine.exitCode = 2
	engine.stderr = "the pokemon is not a valid base64"

	output, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if err == nil {
		t.Fatalf("the failed command returned no error: %q", output)
	}

	if err.Error() != "CoreConsole exited with code 2" {
		t.Fatalf("unexpected error: %v", err)
	}

	expected, expectedErr := runCoreConsoleOnce(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if expectedErr == nil || expectedErr.Error() != err.Error() {
		t.Fatalf("the one-shot engine returned a different error: %v", expectedErr)
	}

	if output != expected {
		t.Fatalf("the warm engine and the one-shot engine disagree:\n warm: %q\n one shot: %q", output, expected)
	}
}

// TestWarmEngineFallsBackWhenTheErrorTextIsMissing checks that the output of
// a failed command is the same as the one of the one-shot engine even when
// the warm engine does not report the text of the standard error.
func TestWarmEngineFallsBackWhenTheErrorTextIsMissing(t *testing.T) {
	engine := setupFakeEngine(t)
	engine.serveMode = "serve_minimal"
	engine.exitCode = 2
	engine.stderr = "the pokemon is not a valid base64"

	output, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if err == nil {
		t.Fatalf("the failed command returned no error: %q", output)
	}

	if err.Error() != "CoreConsole exited with code 2" {
		t.Fatalf("unexpected error: %v", err)
	}

	expected, expectedErr := runCoreConsoleOnce(context.Background(), "legality", "PKMN", "--generation=Gen7")
	if expectedErr == nil || expectedErr.Error() != err.Error() {
		t.Fatalf("the one-shot engine returned a different error: %v", expectedErr)
	}

	if output != expected {
		t.Fatalf("the warm engine and the one-shot engine disagree:\n warm: %q\n one shot: %q", output, expected)
	}
}

// TestWarmEnginePoolIsBounded checks that the pool never starts more engines
// than the allowed maximum.
func TestWarmEnginePoolIsBounded(t *testing.T) {
	engine := setupFakeEngine(t)

	var wait sync.WaitGroup
	for i := 0; i < 8; i++ {
		wait.Add(1)

		go func() {
			defer wait.Done()

			if _, err := RunCoreConsole(context.Background(), "legality", "PKMN", "--generation=Gen7"); err != nil {
				t.Errorf("the request failed: %v", err)
			}
		}()
	}

	wait.Wait()

	engines := engine.startedEngines(t)
	if len(engines) > warmEnginePoolSize {
		t.Fatalf("the pool started %d engines, the maximum is %d", len(engines), warmEnginePoolSize)
	}
}

// TestBuildServeRequest checks how the flags of the CLI are turned into the
// fields of the request of the warm engine.
func TestBuildServeRequest(t *testing.T) {
	request, ok := buildServeRequest("legalize", "PKMN", []string{"--generation=Gen7", "--version=ORAS", "--legalization-generation=Gen9", "--other-flag"})
	if !ok {
		t.Fatal("the command was not accepted by the warm engine")
	}

	if request.Command != "legalize" || request.Pokemon != "PKMN" {
		t.Fatalf("unexpected request: %+v", request)
	}

	if request.Generation == nil || *request.Generation != "Gen7" {
		t.Fatalf("unexpected generation: %+v", request.Generation)
	}

	if request.Version == nil || *request.Version != "ORAS" {
		t.Fatalf("unexpected version: %+v", request.Version)
	}

	if request.LegalizationGeneration == nil || *request.LegalizationGeneration != "Gen9" {
		t.Fatalf("unexpected legalization generation: %+v", request.LegalizationGeneration)
	}

	if len(request.Args) != 1 || request.Args[0] != "--other-flag" {
		t.Fatalf("unexpected arguments: %+v", request.Args)
	}

	// The commands the serve protocol does not understand keep using the
	// one-shot engine.
	if _, ok := buildServeRequest("version", "", nil); ok {
		t.Fatal("the version command must not use the warm engine")
	}
}

// TestWarmEngineWithTheRealEngine compares the warm engine with the one-shot
// engine using the real coreconsole binary and a real pokemon.
//
// The test is skipped when the engine does not support the serve mode yet:
// the serve mode of the engine is a separate work unit.
func TestWarmEngineWithTheRealEngine(t *testing.T) {
	root := realEngineRoot(t)

	pokemon := realEnginePokemon(t, root)

	originalPool := warmEngines
	warmEngines = newEnginePool(warmEnginePoolSize)

	t.Cleanup(func() {
		warmEngines.close()
		warmEngines = originalPool
	})

	request := serveRequest{
		Command:    "legality",
		Pokemon:    pokemon,
		Generation: stringPointer("Gen7"),
	}

	warm, warmErr := warmEngines.run(context.Background(), request)

	oneShot, oneShotErr := runCoreConsoleOnce(context.Background(), "legality", pokemon, "--generation=Gen7")

	t.Logf("warm engine output: %s", warm.Output)
	t.Logf("one-shot engine output: %s", oneShot)

	if warmErr != nil {
		t.Skipf("the real engine cannot serve the request yet: %v", warmErr)
	}

	if oneShotErr != nil {
		t.Fatalf("the one-shot engine failed: %v", oneShotErr)
	}

	if warm.Output != oneShot {
		t.Fatalf("the warm engine and the one-shot engine disagree:\n warm: %q\n one shot: %q", warm.Output, oneShot)
	}

	if warm.ExitCode != 0 {
		t.Fatalf("the warm engine reported the exit code %d", warm.ExitCode)
	}
}

// realEngineRoot moves the test to the root of the repository, where the
// engine and the pokemons live.
func realEngineRoot(t *testing.T) string {
	t.Helper()

	current, err := os.Getwd()
	if err != nil {
		t.Fatalf("failed to read the working directory: %v", err)
	}

	root, err := filepath.Abs(filepath.Join(current, "..", ".."))
	if err != nil {
		t.Fatalf("failed to resolve the root of the repository: %v", err)
	}

	if err := os.Chdir(root); err != nil {
		t.Fatalf("failed to move to the root of the repository: %v", err)
	}

	t.Cleanup(func() {
		_ = os.Chdir(current)
	})

	return root
}

// realEnginePokemon returns the base64 of the first pokemon of the fixtures.
func realEnginePokemon(t *testing.T, root string) string {
	t.Helper()

	entries, err := os.ReadDir(filepath.Join(root, "pokemons"))
	if err != nil {
		t.Skipf("the pokemons are not available: %v", err)
	}

	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".pk7" {
			continue
		}

		content, err := os.ReadFile(filepath.Join(root, "pokemons", entry.Name()))
		if err != nil {
			t.Fatalf("failed to read the pokemon: %v", err)
		}

		return base64.StdEncoding.EncodeToString(content)
	}

	t.Skip("there are no pokemons to test the engine with")

	return ""
}

// stringPointer returns a pointer to the given value.
func stringPointer(value string) *string {
	return &value
}

// TestWarmEngineWithTheRealEngineOnAFailingCommand compares the warm engine
// with the one-shot engine when the command fails.
func TestWarmEngineWithTheRealEngineOnAFailingCommand(t *testing.T) {
	realEngineRoot(t)

	originalPool := warmEngines
	warmEngines = newEnginePool(warmEnginePoolSize)

	t.Cleanup(func() {
		warmEngines.close()
		warmEngines = originalPool
	})

	warm, warmErr := warmEngines.run(context.Background(), serveRequest{
		Command:    "legality",
		Pokemon:    "not a base64 pokemon",
		Generation: stringPointer("Gen7"),
	})
	if warmErr != nil {
		t.Skipf("the real engine cannot serve the request yet: %v", warmErr)
	}

	warmOutput, warmError := warmResponse(context.Background(), "legality", "not a base64 pokemon", nil, warm)

	oneShotOutput, oneShotError := runCoreConsoleOnce(context.Background(), "legality", "not a base64 pokemon", "--generation=Gen7")

	t.Logf("warm engine output: %q (%v)", warmOutput, warmError)
	t.Logf("one-shot engine output: %q (%v)", oneShotOutput, oneShotError)

	if warmError == nil || oneShotError == nil {
		t.Fatalf("the failing command returned no error: %v / %v", warmError, oneShotError)
	}

	if warmError.Error() != oneShotError.Error() {
		t.Fatalf("the engines disagree about the error:\n warm: %v\n one shot: %v", warmError, oneShotError)
	}

	if warmOutput != oneShotOutput {
		t.Fatalf("the warm engine and the one-shot engine disagree:\n warm: %q\n one shot: %q", warmOutput, oneShotOutput)
	}
}
