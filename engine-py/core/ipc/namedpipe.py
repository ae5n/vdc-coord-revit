import json
from multiprocessing.connection import Listener
from typing import Generator, Tuple, Callable, Dict, Any


class PipeServer:
    """Simple named pipe server using multiprocessing.Listener."""

    def __init__(self, pipe_name: str):
        self.address = rf"\\.\pipe\{pipe_name}"

    def listen(self) -> Generator[Tuple[Dict[str, Any], Callable[[Dict[str, Any]], None]], None, None]:
        with Listener(self.address, family="AF_PIPE") as listener:
            while True:
                connection = listener.accept()
                try:
                    while True:
                        raw = connection.recv_bytes()
                        request = json.loads(raw.decode("utf-8"))

                        def respond(payload: Dict[str, Any]) -> None:
                            message = json.dumps(payload)
                            connection.send_bytes(message.encode("utf-8"))

                        yield request, respond
                except EOFError:
                    pass
                finally:
                    connection.close()
