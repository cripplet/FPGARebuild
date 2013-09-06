using System.Threading;

namespace FPGARebuild.Root {
	class Root {
		static void Main(string[] args) {
			Server.Server server = new Server.Server(auto : true);
			Thread packet_thread = new Thread(new ThreadStart(server.Run));
			packet_thread.Start();

			Board.Board board = new Board.Board(server, "00:01:CA:AA:00:01", full_initialize : true);
			Thread ping_thread = new Thread(new ThreadStart(board.Ping));
			Thread light_show_thread = new Thread(new ThreadStart(board.LightShow));

			// ping_thread.Start();
			// light_show_thread.Start();
			
			for(int i = 0; i < 5; i++) {
				// board.TestFunction("sine");
				// board.Delay(5000);
				board.TestFunction("step");
				board.Delay(5000);
			}
			board.TestFunction("abstract");
			board.Delay(5000);
			board.TestFunction("differentiate");
		}
	}
}