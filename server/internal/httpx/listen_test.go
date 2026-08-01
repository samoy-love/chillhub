package httpx

import "testing"

func TestListenAddrDefaultsToLoopback(t *testing.T) {
	t.Setenv("TEST_LISTEN_ADDR", "")
	if got := ListenAddr("TEST_LISTEN_ADDR", 55777); got != "127.0.0.1:55777" {
		t.Fatalf("default = %q, want 127.0.0.1:55777", got)
	}
}

func TestListenAddrOverrides(t *testing.T) {
	cases := []struct{ env, want string }{
		{"0.0.0.0:9000", "0.0.0.0:9000"},
		{":9000", ":9000"},
		{"9000", ":9000"},
		{"  127.0.0.1:1 ", "127.0.0.1:1"},
		{"[::1]:8080", "[::1]:8080"},
		{"example.internal", "example.internal:55700"},
	}
	for _, c := range cases {
		t.Setenv("TEST_LISTEN_ADDR", c.env)
		if got := ListenAddr("TEST_LISTEN_ADDR", 55700); got != c.want {
			t.Fatalf("ListenAddr(%q) = %q, want %q", c.env, got, c.want)
		}
	}
}
