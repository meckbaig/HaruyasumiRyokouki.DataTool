import numpy as np

from photolib import embed


def test_blob_round_trip_preserves_vector():
    vec = np.array([0.1, -0.2, 0.3], dtype=np.float32)
    restored = embed.from_blob(embed.to_blob(vec))
    assert np.allclose(restored, vec)
    assert restored.dtype == np.float32
