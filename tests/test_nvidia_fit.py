"""The parts of an NVIDIA card are offered only where its card and driver take CUDA 12 (local_hardware.nvidia_fit)."""
from system_core.core.local_hardware import nvidia_fit


def test_a_new_card_and_driver_fit():
    assert nvidia_fit("NVIDIA GeForce RTX 5070, 12.0, 610.88") == (True, "")


def test_an_old_card_is_no_nvidia_card():
    fit, why = nvidia_fit("NVIDIA GeForce GTX 980, 5.2, 581.57")
    assert not fit and "5.2" in why


def test_an_old_driver_is_no_nvidia_card():
    fit, why = nvidia_fit("NVIDIA GeForce RTX 2060, 7.5, 512.95")
    assert not fit and "512.95" in why


def test_a_driver_that_knows_no_compute_cap_is_too_old():
    fit, why = nvidia_fit('Field "compute_cap" is not a valid field to query.')
    assert not fit and "too old" in why


def test_one_fitting_card_of_two_is_enough():
    told = "NVIDIA GeForce GTX 780, 3.5, 560.94\nNVIDIA GeForce RTX 3060, 8.6, 560.94"
    assert nvidia_fit(told) == (True, "")


def test_no_card_named_says_nothing():
    assert nvidia_fit("") == (False, "")
