using System;
using UnityEngine;

namespace App.Abstractions
{
    public abstract class InputAdapter : MonoBehaviour
    {
        public abstract event Action OnBackPressed;
    }
}