const mode = process.env.EMKE_FAKE_FETCH_MODE;

globalThis.fetch = async (input, options = {}) => {
  const url = new URL(input);
  switch (mode) {
    case "untrusted-redirect":
      return new Response(null, {
        status: 302,
        headers: { location: "https://example.invalid/corpus.txt" },
      });
    case "allowed-redirect":
      if (url.pathname === "/cache/direct.txt") {
        return new Response("abc", {
          status: 200,
          headers: { "content-length": "3" },
        });
      }
      return new Response(null, {
        status: 302,
        headers: {
          location: "https://www.gutenberg.org/cache/direct.txt",
        },
      });
    case "large-content-length":
      return new Response("x", {
        status: 200,
        headers: { "content-length": "1000" },
      });
    case "large-stream":
      return new Response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(new Uint8Array([1, 2, 3]));
            controller.enqueue(new Uint8Array([4, 5, 6]));
            controller.close();
          },
        }),
        { status: 200 },
      );
    case "timeout":
      if (!options.signal) {
        throw new Error("missing AbortSignal");
      }

      return new Promise((resolve, reject) => {
        void resolve;
        const rejectForAbort = () =>
          reject(new DOMException("aborted", "AbortError"));
        if (options.signal.aborted) {
          rejectForAbort();
        } else {
          options.signal.addEventListener("abort", rejectForAbort, {
            once: true,
          });
        }
      });
    default:
      throw new Error(`Unknown fake fetch mode: ${mode}`);
  }
};
