"""The dictionary of SETTINGS of the window of 3.0 (config/dictionary.txt) as the term_dictionary of the engine of files."""

from system_core.core.paths import get_project_paths
from system_core.core.terms import read_dictionary_file, shared_keyterms
from system_core.settings import load_settings


def test_dictionary_file_gives_terms_and_written_forms(tmp_path):
    file = tmp_path / "dictionary.txt"
    file.write_text(
        "# заметка\nФГИС ТП\nгитхаб экшнс -> GitHub Actions\nкилометров в час => км/ч\n"
        "градусов цельсия → °C\n-> пусто\nфгис тп\n",
        encoding="utf-8",
    )
    assert read_dictionary_file(file) == ("ФГИС ТП", "GitHub Actions", "км/ч", "°C")
    assert read_dictionary_file(tmp_path / "none.txt") is None


def test_dictionary_file_takes_the_place_of_term_dictionary(tmp_path):
    paths = get_project_paths(tmp_path)
    paths.config.mkdir(parents=True, exist_ok=True)
    (paths.config / "app_settings.yaml").write_text("term_dictionary:\n- Старый термин\n", encoding="utf-8")
    assert shared_keyterms(load_settings(paths)) == ("Старый термин",)

    (paths.config / "dictionary.txt").write_text("Kubernetes\n", encoding="utf-8")
    assert shared_keyterms(load_settings(paths)) == ("Kubernetes",)
