using System;
using UnityEngine;

namespace Exerussus.AppCore.Input
{
    public abstract class InputAdapter : MonoBehaviour
    {
        public abstract event Action OnBackPressed;
    }
}
