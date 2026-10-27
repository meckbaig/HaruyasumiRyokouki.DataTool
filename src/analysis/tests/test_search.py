import numpy as np

from photolib import db, embed, search


def test_cosine_search_ranks_nearest_first(tmp_path):
    conn = db.connect(str(tmp_path / "t.sqlite"))
    db.init_db(conn)
    for i, path in enumerate(["/a.jpg", "/b.jpg", "/c.jpg"]):
        db.upsert_image(conn, path, path, "g", None, "unknown")
    conn.commit()
    vectors = {
        1: np.array([1.0, 0.0], dtype=np.float32),
        2: np.array([0.0, 1.0], dtype=np.float32),
        3: np.array([0.7, 0.7], dtype=np.float32),
    }
    for image_id, vec in vectors.items():
        conn.execute(
            "INSERT INTO embeddings(image_id, model, dim, vector) VALUES (?,?,?,?)",
            (image_id, "m", 2, embed.to_blob(vec / np.linalg.norm(vec))),
        )
    conn.commit()

    results = search.cosine_search(conn, np.array([1.0, 0.0], dtype=np.float32), k=2)
    assert results[0][0] == 1
    assert results[1][0] == 3
